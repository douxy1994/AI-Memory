import CryptoKit
import Foundation
import SQLite3

private let nativeWriterSQLiteTransient = unsafeBitCast(
    -1,
    to: sqlite3_destructor_type.self
)

struct NativeAgentWriteResult: Sendable {
    let id: String
    let storagePath: String
    let resumeCommand: String?
}

enum NativeAgentConversationWriterError: LocalizedError {
    case unsupportedTarget(AgentKind)
    case missingStore(String)
    case invalidStore(String)
    case sqlite(String)

    var errorDescription: String? {
        switch self {
        case .unsupportedTarget(let agent):
            "\(agent.label) 的原生会话格式不支持安全写入。"
        case .missingStore(let path):
            "目标 Agent 尚未创建本地数据存储：\(path)"
        case .invalidStore(let reason):
            "目标 Agent 数据存储不兼容：\(reason)"
        case .sqlite(let message):
            "写入目标 Agent 数据库失败：\(message)"
        }
    }
}

/// Writes a migrated conversation to the target agent's real native history.
///
/// The emitted formats mirror ChatMem's supported writer set. Agents whose
/// stores are undocumented or read-only are rejected instead of reporting a
/// false migration success.
actor NativeAgentConversationWriter {
    static let writableTargets: [AgentKind] = [.claude, .codex, .gemini, .opencode]

    private let home: URL
    private let fileManager: FileManager
    private let encoder: JSONEncoder

    init(
        home: URL = FileManager.default.homeDirectoryForCurrentUser,
        fileManager: FileManager = .default
    ) {
        self.home = home
        self.fileManager = fileManager
        self.encoder = JSONEncoder()
    }

    func write(
        _ conversation: ConversationDetail,
        to target: AgentKind
    ) throws -> NativeAgentWriteResult {
        switch target {
        case .claude:
            return try writeClaude(conversation)
        case .codex:
            return try writeCodex(conversation)
        case .gemini:
            return try writeGemini(conversation)
        case .opencode:
            return try writeOpenCode(conversation)
        case .antigravity, .zcode, .hermes, .kimi:
            throw NativeAgentConversationWriterError.unsupportedTarget(target)
        }
    }

    func discardWritten(_ result: NativeAgentWriteResult, target: AgentKind) throws {
        switch target {
        case .claude, .gemini:
            if fileManager.fileExists(atPath: result.storagePath) {
                try fileManager.removeItem(atPath: result.storagePath)
            }
        case .codex:
            try deleteCodexThread(id: result.id, moveFileToTrash: false)
        case .opencode:
            try deleteOpenCodeSession(id: result.id)
        case .antigravity, .zcode, .hermes, .kimi:
            return
        }
    }

    /// Removes the verified source from its real agent store for a cut migration.
    /// File-backed histories go through the macOS Trash so the operation remains
    /// recoverable outside AI Memory.
    func archiveSource(_ conversation: ConversationDetail) throws {
        guard let agent = AgentKind(rawValue: conversation.sourceAgent) else {
            throw NativeAgentConversationWriterError.invalidStore(
                "未知源 Agent：\(conversation.sourceAgent)"
            )
        }
        switch agent {
        case .claude, .gemini, .antigravity, .zcode, .kimi:
            guard let storagePath = conversation.storagePath, !storagePath.isEmpty else {
                throw NativeAgentConversationWriterError.invalidStore(
                    "\(agent.label) 会话缺少原始存储路径。"
                )
            }
            let url = URL(fileURLWithPath: storagePath)
            guard fileManager.fileExists(atPath: url.path) else {
                throw NativeAgentConversationWriterError.missingStore(url.path)
            }
            var resultingURL: NSURL?
            try fileManager.trashItem(at: url, resultingItemURL: &resultingURL)
        case .codex:
            try deleteCodexThread(id: conversation.id, moveFileToTrash: true)
        case .opencode:
            try archiveOpenCodeSession(id: conversation.id)
        case .hermes:
            throw NativeAgentConversationWriterError.unsupportedTarget(.hermes)
        }
    }

    /// Restores a source previously archived by `archiveSource` for AI
    /// Memory's recoverable Trash workflow. OpenCode can be unarchived in
    /// place; Claude, Codex, and Gemini are written back through their native
    /// formats and may receive a new conversation id, matching ChatMem's
    /// adapter-based restore behavior.
    func restoreArchivedSource(
        _ conversation: ConversationDetail
    ) throws -> NativeAgentWriteResult {
        guard let agent = AgentKind(rawValue: conversation.sourceAgent) else {
            throw NativeAgentConversationWriterError.invalidStore(
                "未知源 Agent：\(conversation.sourceAgent)"
            )
        }
        switch agent {
        case .opencode:
            try unarchiveOpenCodeSession(id: conversation.id)
            return NativeAgentWriteResult(
                id: conversation.id,
                storagePath: try openCodeDatabaseURL().path,
                resumeCommand: conversation.resumeCommand
            )
        case .claude, .codex, .gemini:
            return try write(conversation, to: agent)
        case .antigravity, .zcode, .hermes, .kimi:
            throw NativeAgentConversationWriterError.unsupportedTarget(agent)
        }
    }

    // MARK: - Claude Code

    private func writeClaude(_ conversation: ConversationDetail) throws -> NativeAgentWriteResult {
        let sessionID = UUID().uuidString.lowercased()
        let cwd = normalizedProjectDirectory(conversation.projectDir)
        let encodedProject = cwd.map { Self.claudePathCharacter($0) }
        let directory = home
            .appendingPathComponent(".claude/projects", isDirectory: true)
            .appendingPathComponent(String(encodedProject), isDirectory: true)
        try fileManager.createDirectory(at: directory, withIntermediateDirectories: true)
        let destination = directory.appendingPathComponent("\(sessionID).jsonl")

        var events: [[String: Any]] = []
        var parentUUID: String?
        if !conversation.fileChanges.isEmpty {
            var backups: [String: Any] = [:]
            for change in conversation.fileChanges {
                backups[change.path] = [
                    "backupFileName": NSNull(),
                    "version": 1,
                    "backupTime": change.timestamp,
                ]
            }
            events.append([
                "type": "file-history-snapshot",
                "snapshot": [
                    "trackedFileBackups": backups,
                    "timestamp": conversation.createdAt,
                ],
            ])
        }

        for message in conversation.messages {
            let eventID = UUID().uuidString.lowercased()
            if message.role.lowercased() == "assistant" {
                var blocks: [[String: Any]] = []
                if !message.content.isEmpty {
                    blocks.append(["type": "text", "text": message.content])
                }
                var results: [(String, [String: Any])] = []
                for tool in message.toolCalls {
                    let toolID = "toolu_\(UUID().uuidString.replacingOccurrences(of: "-", with: ""))"
                    blocks.append([
                        "type": "tool_use",
                        "id": toolID,
                        "name": tool.name,
                        "input": Self.foundationValue(tool.input),
                    ])
                    let resultID = UUID().uuidString.lowercased()
                    results.append((resultID, [
                        "type": "user",
                        "uuid": resultID,
                        "timestamp": message.timestamp,
                        "sessionId": sessionID,
                        "cwd": cwd,
                        "isSidechain": false,
                        "message": [
                            "role": "user",
                            "content": [[
                                "type": "tool_result",
                                "tool_use_id": toolID,
                                "content": tool.output ?? "",
                                "is_error": tool.status.lowercased() == "error",
                            ]],
                        ],
                    ]))
                }
                if blocks.isEmpty { blocks.append(["type": "text", "text": ""]) }
                var event: [String: Any] = [
                    "type": "assistant",
                    "uuid": eventID,
                    "timestamp": message.timestamp,
                    "sessionId": sessionID,
                    "cwd": cwd,
                    "isSidechain": false,
                    "message": [
                        "role": "assistant",
                        "id": "msg_\(UUID().uuidString.replacingOccurrences(of: "-", with: ""))",
                        "content": blocks,
                    ],
                ]
                if let parentUUID { event["parentUuid"] = parentUUID }
                events.append(event)
                parentUUID = eventID
                for (resultID, var result) in results {
                    if let parentUUID { result["parentUuid"] = parentUUID }
                    events.append(result)
                    parentUUID = resultID
                }
            } else {
                var event: [String: Any] = [
                    "type": "user",
                    "uuid": eventID,
                    "timestamp": message.timestamp,
                    "sessionId": sessionID,
                    "cwd": cwd,
                    "isSidechain": false,
                    "message": ["role": "user", "content": message.content],
                ]
                if let parentUUID { event["parentUuid"] = parentUUID }
                events.append(event)
                parentUUID = eventID
            }
        }
        if let summary = conversation.summary, !summary.isEmpty {
            events.append([
                "type": "summary",
                "summary": summary,
                "leafUuid": parentUUID ?? UUID().uuidString.lowercased(),
            ])
        }
        try Self.writeJSONLines(events, to: destination)
        return NativeAgentWriteResult(
            id: sessionID,
            storagePath: destination.path,
            resumeCommand: "claude --resume \(sessionID)"
        )
    }

    // MARK: - Gemini CLI

    private func writeGemini(_ conversation: ConversationDetail) throws -> NativeAgentWriteResult {
        let sessionID = UUID().uuidString.lowercased()
        let projectHash: String
        if conversation.projectDir.hasPrefix("gemini:") {
            projectHash = String(conversation.projectDir.dropFirst("gemini:".count))
        } else {
            projectHash = SHA256.hash(data: Data(conversation.projectDir.utf8))
                .map { String(format: "%02x", $0) }
                .joined()
        }
        let directory = home
            .appendingPathComponent(".gemini/tmp", isDirectory: true)
            .appendingPathComponent(projectHash, isDirectory: true)
            .appendingPathComponent("chats", isDirectory: true)
        try fileManager.createDirectory(at: directory, withIntermediateDirectories: true)
        let destination = directory.appendingPathComponent("session-\(sessionID).json")

        let messages: [[String: Any]] = conversation.messages.map { message in
            let role = message.role.lowercased()
            if role == "assistant" {
                let tools = message.toolCalls.map { tool -> [String: Any] in
                    let toolID = UUID().uuidString.lowercased()
                    let responseKey = tool.status.lowercased() == "error" ? "error" : "output"
                    return [
                        "id": toolID,
                        "name": tool.name,
                        "args": Self.foundationValue(tool.input),
                        "resultDisplay": tool.output ?? "",
                        "result": [[
                            "functionResponse": [
                                "id": toolID,
                                "name": tool.name,
                                "response": [responseKey: tool.output ?? ""],
                            ],
                        ]],
                        "status": tool.status.lowercased() == "error" ? "error" : "success",
                    ]
                }
                return [
                    "id": UUID().uuidString.lowercased(),
                    "timestamp": message.timestamp,
                    "type": "gemini",
                    "content": message.content,
                    "model": Self.metadataString(message.metadata, key: "model") ?? "imported",
                    "thoughts": [],
                    "toolCalls": tools,
                ]
            }
            return [
                "id": UUID().uuidString.lowercased(),
                "timestamp": message.timestamp,
                "type": role == "user" ? "user" : "info",
                "content": message.content,
            ]
        }
        let object: [String: Any] = [
            "sessionId": sessionID,
            "projectHash": projectHash,
            "projectPath": conversation.projectDir,
            "startTime": conversation.createdAt,
            "lastUpdated": conversation.updatedAt,
            "summary": conversation.summary ?? NSNull(),
            "messages": messages,
        ]
        try Self.writeJSONObject(object, to: destination)
        return NativeAgentWriteResult(
            id: sessionID,
            storagePath: destination.path,
            resumeCommand: nil
        )
    }

    // MARK: - Codex

    private func writeCodex(_ conversation: ConversationDetail) throws -> NativeAgentWriteResult {
        let codexRoot = home.appendingPathComponent(".codex", isDirectory: true)
        try fileManager.createDirectory(at: codexRoot, withIntermediateDirectories: true)
        let databaseURL = codexRoot.appendingPathComponent("state_5.sqlite")
        let threadID = UUID().uuidString.lowercased()
        let created = Self.date(conversation.createdAt)
        let components = Calendar(identifier: .gregorian).dateComponents(
            [.year, .month, .day],
            from: created
        )
        let directory = codexRoot
            .appendingPathComponent("sessions", isDirectory: true)
            .appendingPathComponent(String(format: "%04d", components.year ?? 1970), isDirectory: true)
            .appendingPathComponent(String(format: "%02d", components.month ?? 1), isDirectory: true)
            .appendingPathComponent(String(format: "%02d", components.day ?? 1), isDirectory: true)
        try fileManager.createDirectory(at: directory, withIntermediateDirectories: true)
        let timestamp = Self.rolloutDateFormatter.string(from: created)
        let rolloutURL = directory.appendingPathComponent(
            "rollout-\(timestamp)-\(threadID).jsonl"
        )

        let defaults = try codexDefaults(databaseURL: databaseURL)
        var events: [[String: Any]] = [[
            "timestamp": conversation.createdAt,
            "type": "session_meta",
            "payload": [
                "id": threadID,
                "timestamp": conversation.createdAt,
                "cwd": conversation.projectDir,
                "originator": "AI Memory",
                "cli_version": defaults.cliVersion,
                "source": defaults.source,
                "model_provider": defaults.modelProvider,
            ],
        ]]
        var firstUser = ""
        for message in conversation.messages {
            let role = message.role.lowercased()
            if role == "assistant" {
                if !message.content.isEmpty {
                    events.append([
                        "timestamp": message.timestamp,
                        "type": "event_msg",
                        "payload": [
                            "type": "agent_message",
                            "message": message.content,
                            "phase": "commentary",
                            "memory_citation": NSNull(),
                        ],
                    ])
                    events.append([
                        "timestamp": message.timestamp,
                        "type": "response_item",
                        "payload": [
                            "type": "message",
                            "role": "assistant",
                            "content": [["type": "output_text", "text": message.content]],
                            "phase": "commentary",
                        ],
                    ])
                }
                for tool in message.toolCalls {
                    let callID = "call_\(UUID().uuidString.replacingOccurrences(of: "-", with: ""))"
                    let input = Self.foundationValue(tool.input)
                    if let textInput = input as? String {
                        events.append([
                            "timestamp": message.timestamp,
                            "type": "response_item",
                            "payload": [
                                "type": "custom_tool_call",
                                "status": "completed",
                                "call_id": callID,
                                "name": tool.name,
                                "input": textInput,
                            ],
                        ])
                        events.append([
                            "timestamp": message.timestamp,
                            "type": "response_item",
                            "payload": [
                                "type": "custom_tool_call_output",
                                "call_id": callID,
                                "output": tool.output ?? "",
                            ],
                        ])
                    } else {
                        let arguments = try Self.jsonString(input)
                        events.append([
                            "timestamp": message.timestamp,
                            "type": "response_item",
                            "payload": [
                                "type": "function_call",
                                "name": tool.name,
                                "arguments": arguments,
                                "call_id": callID,
                            ],
                        ])
                        events.append([
                            "timestamp": message.timestamp,
                            "type": "response_item",
                            "payload": [
                                "type": "function_call_output",
                                "call_id": callID,
                                "output": tool.output ?? "",
                            ],
                        ])
                    }
                }
            } else {
                let responseRole = role == "system" ? "developer" : "user"
                events.append([
                    "timestamp": message.timestamp,
                    "type": "response_item",
                    "payload": [
                        "type": "message",
                        "role": responseRole,
                        "content": [["type": "input_text", "text": message.content]],
                    ],
                ])
                events.append([
                    "timestamp": message.timestamp,
                    "type": "event_msg",
                    "payload": [
                        "type": "user_message",
                        "message": message.content,
                        "images": [],
                        "local_images": [],
                        "text_elements": [],
                    ],
                ])
                if role == "user", firstUser.isEmpty { firstUser = message.content }
            }
        }
        try Self.writeJSONLines(events, to: rolloutURL)

        do {
            try withSQLite(databaseURL, create: true) { database in
                try Self.createCodexSchema(database)
                try Self.ensureCodexOptionalColumns(database)
                let title = Self.title(conversation, firstUser: firstUser)
                let createdSeconds = Int64(Self.date(conversation.createdAt).timeIntervalSince1970)
                let updatedSeconds = Int64(Self.date(conversation.updatedAt).timeIntervalSince1970)
                try Self.execute(
                    database,
                    """
                    INSERT INTO threads (
                      id, rollout_path, created_at, updated_at, source, model_provider,
                      cwd, title, sandbox_policy, approval_mode, tokens_used,
                      has_user_event, archived, git_branch, cli_version,
                      first_user_message, memory_mode, model, reasoning_effort,
                      agent_path, created_at_ms, updated_at_ms, thread_source, preview
                    ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 0, ?, 0, NULL, ?, ?, 'enabled',
                              ?, ?, NULL, ?, ?, 'user', ?);
                    """,
                    bindings: [
                        .text(threadID), .text(rolloutURL.path),
                        .integer(createdSeconds), .integer(updatedSeconds),
                        .text(defaults.source), .text(defaults.modelProvider),
                        .text(conversation.projectDir), .text(title),
                        .text(defaults.sandboxPolicy), .text(defaults.approvalMode),
                        .integer(defaults.hasUserEvent), .text(defaults.cliVersion),
                        .text(firstUser), defaults.model.map(SQLiteBinding.text) ?? .null,
                        defaults.reasoningEffort.map(SQLiteBinding.text) ?? .null,
                        .integer(createdSeconds * 1_000), .integer(updatedSeconds * 1_000),
                        .text(firstUser.isEmpty ? title : firstUser),
                    ]
                )
            }
        } catch {
            try? fileManager.removeItem(at: rolloutURL)
            throw error
        }
        return NativeAgentWriteResult(
            id: threadID,
            storagePath: rolloutURL.path,
            resumeCommand: "codex resume \(threadID)"
        )
    }

    // MARK: - OpenCode

    private func writeOpenCode(_ conversation: ConversationDetail) throws -> NativeAgentWriteResult {
        let databaseURL = try openCodeDatabaseURL()
        return try withSQLite(databaseURL, create: false) { database in
            guard try Self.tableExists(database, name: "session"),
                  try Self.tableExists(database, name: "message"),
                  try Self.tableExists(database, name: "part"),
                  try Self.tableExists(database, name: "project") else {
                throw NativeAgentConversationWriterError.invalidStore(
                    "OpenCode 数据库缺少 session/message/part/project 表。"
                )
            }
            try Self.exec(database, "BEGIN IMMEDIATE TRANSACTION;")
            do {
                let created = Self.milliseconds(conversation.createdAt)
                let updated = Self.milliseconds(conversation.updatedAt)
                let cwd = conversation.projectDir.trimmingCharacters(in: .whitespacesAndNewlines)
                    .isEmpty ? "." : conversation.projectDir
                let projectID = try Self.openCodeProjectID(
                    database, cwd: cwd, timestamp: created
                )
                let sessionID = Self.compactID("ses")
                let title = Self.title(conversation, firstUser: "")
                let version = try Self.scalarText(
                    database,
                    "SELECT version FROM session WHERE version != '' ORDER BY time_updated DESC LIMIT 1;"
                ) ?? "0.0.0"
                let fileCount = Set(conversation.fileChanges.map(\.path)).count
                try Self.execute(
                    database,
                    """
                    INSERT INTO session (
                      id, project_id, slug, directory, title, version,
                      summary_files, time_created, time_updated
                    ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?);
                    """,
                    bindings: [
                        .text(sessionID), .text(projectID),
                        .text(Self.slug(title)), .text(cwd), .text(title),
                        .text(version), .integer(Int64(fileCount)),
                        .integer(created), .integer(updated),
                    ]
                )
                var parentID: String?
                for message in conversation.messages {
                    let messageID = Self.compactID("msg")
                    let timestamp = Self.milliseconds(message.timestamp)
                    let role = message.role.lowercased()
                    var data: [String: Any] = [
                        "role": role,
                        "time": ["created": timestamp],
                        "path": ["cwd": cwd, "root": cwd],
                        "source": "aimemory",
                    ]
                    if role == "assistant" {
                        data["time"] = ["created": timestamp, "completed": timestamp]
                        data["providerID"] = "aimemory"
                        data["modelID"] = "imported"
                        data["agent"] = "build"
                    }
                    if let parentID { data["parentID"] = parentID }
                    try Self.execute(
                        database,
                        """
                        INSERT INTO message (id, session_id, time_created, time_updated, data)
                        VALUES (?, ?, ?, ?, ?);
                        """,
                        bindings: [
                            .text(messageID), .text(sessionID),
                            .integer(timestamp), .integer(timestamp),
                            .text(try Self.jsonString(data)),
                        ]
                    )
                    if !message.content.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                        try Self.insertOpenCodePart(
                            database, messageID: messageID, sessionID: sessionID,
                            timestamp: timestamp,
                            data: ["type": "text", "text": message.content]
                        )
                    }
                    for tool in message.toolCalls {
                        let output: Any = tool.output ?? NSNull()
                        try Self.insertOpenCodePart(
                            database, messageID: messageID, sessionID: sessionID,
                            timestamp: timestamp,
                            data: [
                                "type": "tool",
                                "callID": Self.compactID("call"),
                                "tool": tool.name,
                                "state": [
                                    "status": tool.status.lowercased() == "error" ? "error" : "completed",
                                    "input": Self.foundationValue(tool.input),
                                    "output": output,
                                    "metadata": [:],
                                    "time": ["start": timestamp, "end": timestamp],
                                ],
                            ]
                        )
                    }
                    let changed = conversation.fileChanges
                        .filter { $0.messageId == message.id }
                        .map(\.path)
                    if !changed.isEmpty {
                        try Self.insertOpenCodePart(
                            database, messageID: messageID, sessionID: sessionID,
                            timestamp: timestamp,
                            data: [
                                "type": "patch",
                                "hash": Self.compactID("patch"),
                                "files": changed,
                            ]
                        )
                    }
                    parentID = messageID
                }
                try Self.exec(database, "COMMIT;")
                return NativeAgentWriteResult(
                    id: sessionID,
                    storagePath: databaseURL.path,
                    resumeCommand: nil
                )
            } catch {
                try? Self.exec(database, "ROLLBACK;")
                throw error
            }
        }
    }

    private func openCodeDatabaseURL() throws -> URL {
        let directory = home.appendingPathComponent(".local/share/opencode", isDirectory: true)
        let candidates = [
            directory.appendingPathComponent("opencode.db"),
            directory.appendingPathComponent("opencode.sqlite"),
        ]
        if let existing = candidates.first(where: { fileManager.fileExists(atPath: $0.path) }) {
            return existing
        }
        if fileManager.fileExists(atPath: directory.path),
           let values = try? fileManager.contentsOfDirectory(
            at: directory,
            includingPropertiesForKeys: nil
           ),
           let existing = values.first(where: {
               $0.lastPathComponent.hasPrefix("opencode")
                   && ["db", "sqlite", "sqlite3"].contains($0.pathExtension.lowercased())
           }) {
            return existing
        }
        throw NativeAgentConversationWriterError.missingStore(candidates[0].path)
    }

    private func deleteCodexThread(id: String, moveFileToTrash: Bool) throws {
        let databaseURL = home.appendingPathComponent(".codex/state_5.sqlite")
        guard fileManager.fileExists(atPath: databaseURL.path) else {
            throw NativeAgentConversationWriterError.missingStore(databaseURL.path)
        }
        try withSQLite(databaseURL, create: false) { database in
            let escaped = id.replacingOccurrences(of: "'", with: "''")
            let rollout = try Self.scalarText(
                database,
                "SELECT rollout_path FROM threads WHERE id = '\(escaped)' LIMIT 1;"
            )
            guard let rollout else {
                throw NativeAgentConversationWriterError.invalidStore(
                    "Codex 会话不存在：\(id)"
                )
            }
            let rolloutURL = URL(fileURLWithPath: rollout)
            if fileManager.fileExists(atPath: rolloutURL.path) {
                if moveFileToTrash {
                    var resultingURL: NSURL?
                    try fileManager.trashItem(at: rolloutURL, resultingItemURL: &resultingURL)
                } else {
                    try fileManager.removeItem(at: rolloutURL)
                }
            }
            try Self.execute(
                database,
                "DELETE FROM threads WHERE id = ?;",
                bindings: [.text(id)]
            )
        }
    }

    private func archiveOpenCodeSession(id: String) throws {
        let databaseURL = try openCodeDatabaseURL()
        try withSQLite(databaseURL, create: false) { database in
            try Self.execute(
                database,
                "UPDATE session SET time_archived = ?, time_updated = ? WHERE id = ?;",
                bindings: [
                    .integer(Int64(Date().timeIntervalSince1970 * 1_000)),
                    .integer(Int64(Date().timeIntervalSince1970 * 1_000)),
                    .text(id),
                ]
            )
            guard sqlite3_changes(database) > 0 else {
                throw NativeAgentConversationWriterError.invalidStore(
                    "OpenCode 会话不存在：\(id)"
                )
            }
        }
    }

    private func unarchiveOpenCodeSession(id: String) throws {
        let databaseURL = try openCodeDatabaseURL()
        try withSQLite(databaseURL, create: false) { database in
            try Self.execute(
                database,
                "UPDATE session SET time_archived = NULL, time_updated = ? WHERE id = ?;",
                bindings: [
                    .integer(Int64(Date().timeIntervalSince1970 * 1_000)),
                    .text(id),
                ]
            )
            guard sqlite3_changes(database) > 0 else {
                throw NativeAgentConversationWriterError.invalidStore(
                    "OpenCode 会话不存在：\(id)"
                )
            }
        }
    }

    private func deleteOpenCodeSession(id: String) throws {
        let databaseURL = try openCodeDatabaseURL()
        try withSQLite(databaseURL, create: false) { database in
            try Self.execute(
                database,
                "DELETE FROM session WHERE id = ?;",
                bindings: [.text(id)]
            )
        }
    }

    // MARK: - SQLite helpers

    private struct CodexDefaults {
        var source = "vscode"
        var modelProvider = "openai"
        var sandboxPolicy = #"{"type":"workspace-write"}"#
        var approvalMode = "on-request"
        var cliVersion = ""
        var model: String?
        var reasoningEffort: String?
        var hasUserEvent: Int64 = 0
    }

    private func codexDefaults(databaseURL: URL) throws -> CodexDefaults {
        guard fileManager.fileExists(atPath: databaseURL.path) else {
            return CodexDefaults()
        }
        return try withSQLite(databaseURL, create: false) { database in
            guard try Self.tableExists(database, name: "threads") else {
                return CodexDefaults()
            }
            let rows = try Self.rows(
                database,
                """
                SELECT source, model_provider, sandbox_policy, approval_mode,
                       cli_version, model, reasoning_effort, has_user_event
                FROM threads WHERE source = 'vscode'
                ORDER BY updated_at DESC LIMIT 1;
                """
            )
            guard let row = rows.first else { return CodexDefaults() }
            var value = CodexDefaults()
            value.source = row["source"] as? String ?? value.source
            value.modelProvider = row["model_provider"] as? String ?? value.modelProvider
            value.sandboxPolicy = row["sandbox_policy"] as? String ?? value.sandboxPolicy
            value.approvalMode = row["approval_mode"] as? String ?? value.approvalMode
            value.cliVersion = row["cli_version"] as? String ?? ""
            value.model = row["model"] as? String
            value.reasoningEffort = row["reasoning_effort"] as? String
            value.hasUserEvent = row["has_user_event"] as? Int64 ?? 0
            return value
        }
    }

    private func withSQLite<T>(
        _ url: URL,
        create: Bool,
        body: (OpaquePointer) throws -> T
    ) throws -> T {
        var database: OpaquePointer?
        let flags = SQLITE_OPEN_READWRITE | SQLITE_OPEN_FULLMUTEX
            | (create ? SQLITE_OPEN_CREATE : 0)
        guard sqlite3_open_v2(url.path, &database, flags, nil) == SQLITE_OK,
              let database else {
            let message = database.map { String(cString: sqlite3_errmsg($0)) } ?? url.path
            if let database { sqlite3_close(database) }
            throw NativeAgentConversationWriterError.sqlite(message)
        }
        defer { sqlite3_close(database) }
        try Self.exec(database, "PRAGMA foreign_keys = ON;")
        return try body(database)
    }

    private enum SQLiteBinding {
        case text(String)
        case integer(Int64)
        case null
    }

    private static func execute(
        _ database: OpaquePointer,
        _ sql: String,
        bindings: [SQLiteBinding]
    ) throws {
        var statement: OpaquePointer?
        guard sqlite3_prepare_v2(database, sql, -1, &statement, nil) == SQLITE_OK,
              let statement else {
            throw NativeAgentConversationWriterError.sqlite(
                String(cString: sqlite3_errmsg(database))
            )
        }
        defer { sqlite3_finalize(statement) }
        for (offset, binding) in bindings.enumerated() {
            let index = Int32(offset + 1)
            switch binding {
            case .text(let value):
                sqlite3_bind_text(statement, index, value, -1, nativeWriterSQLiteTransient)
            case .integer(let value):
                sqlite3_bind_int64(statement, index, value)
            case .null:
                sqlite3_bind_null(statement, index)
            }
        }
        guard sqlite3_step(statement) == SQLITE_DONE else {
            throw NativeAgentConversationWriterError.sqlite(
                String(cString: sqlite3_errmsg(database))
            )
        }
    }

    private static func exec(_ database: OpaquePointer, _ sql: String) throws {
        var error: UnsafeMutablePointer<CChar>?
        guard sqlite3_exec(database, sql, nil, nil, &error) == SQLITE_OK else {
            let message = error.map { String(cString: $0) }
                ?? String(cString: sqlite3_errmsg(database))
            sqlite3_free(error)
            throw NativeAgentConversationWriterError.sqlite(message)
        }
    }

    private static func rows(
        _ database: OpaquePointer,
        _ sql: String
    ) throws -> [[String: Any]] {
        var statement: OpaquePointer?
        guard sqlite3_prepare_v2(database, sql, -1, &statement, nil) == SQLITE_OK,
              let statement else {
            throw NativeAgentConversationWriterError.sqlite(
                String(cString: sqlite3_errmsg(database))
            )
        }
        defer { sqlite3_finalize(statement) }
        var output: [[String: Any]] = []
        while sqlite3_step(statement) == SQLITE_ROW {
            var row: [String: Any] = [:]
            for index in 0..<sqlite3_column_count(statement) {
                let name = String(cString: sqlite3_column_name(statement, index))
                switch sqlite3_column_type(statement, index) {
                case SQLITE_INTEGER:
                    row[name] = sqlite3_column_int64(statement, index)
                case SQLITE_TEXT:
                    row[name] = String(cString: sqlite3_column_text(statement, index))
                default:
                    break
                }
            }
            output.append(row)
        }
        return output
    }

    private static func scalarText(
        _ database: OpaquePointer,
        _ sql: String
    ) throws -> String? {
        try rows(database, sql).first?.values.first as? String
    }

    private static func tableExists(_ database: OpaquePointer, name: String) throws -> Bool {
        let safe = name.replacingOccurrences(of: "'", with: "''")
        return !(try rows(
            database,
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name = '\(safe)' LIMIT 1;"
        )).isEmpty
    }

    private static func createCodexSchema(_ database: OpaquePointer) throws {
        try exec(
            database,
            """
            CREATE TABLE IF NOT EXISTS threads (
              id TEXT PRIMARY KEY, rollout_path TEXT NOT NULL,
              created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL,
              source TEXT NOT NULL, model_provider TEXT NOT NULL, cwd TEXT NOT NULL,
              title TEXT NOT NULL, sandbox_policy TEXT NOT NULL,
              approval_mode TEXT NOT NULL, tokens_used INTEGER NOT NULL DEFAULT 0,
              has_user_event INTEGER NOT NULL DEFAULT 0, archived INTEGER NOT NULL DEFAULT 0,
              archived_at INTEGER, git_sha TEXT, git_branch TEXT, git_origin_url TEXT,
              cli_version TEXT NOT NULL DEFAULT '', first_user_message TEXT NOT NULL DEFAULT '',
              agent_nickname TEXT, agent_role TEXT, memory_mode TEXT NOT NULL DEFAULT 'enabled',
              model TEXT, reasoning_effort TEXT, agent_path TEXT,
              created_at_ms INTEGER, updated_at_ms INTEGER, thread_source TEXT, preview TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_threads_created_at
              ON threads(created_at DESC, id DESC);
            """
        )
    }

    private static func ensureCodexOptionalColumns(_ database: OpaquePointer) throws {
        let existing = Set(try rows(database, "PRAGMA table_info(threads);").compactMap {
            $0["name"] as? String
        })
        let columns = [
            ("model", "TEXT"), ("reasoning_effort", "TEXT"), ("agent_path", "TEXT"),
            ("created_at_ms", "INTEGER"), ("updated_at_ms", "INTEGER"),
            ("thread_source", "TEXT"), ("preview", "TEXT"),
        ]
        for (name, type) in columns where !existing.contains(name) {
            try exec(database, "ALTER TABLE threads ADD COLUMN \(name) \(type);")
        }
    }

    private static func openCodeProjectID(
        _ database: OpaquePointer,
        cwd: String,
        timestamp: Int64
    ) throws -> String {
        let escaped = cwd.replacingOccurrences(of: "'", with: "''")
        if let id = try scalarText(
            database,
            "SELECT id FROM project WHERE worktree = '\(escaped)' ORDER BY time_updated DESC LIMIT 1;"
        ) {
            return id
        }
        let projectID = compactID("project")
        let name = URL(fileURLWithPath: cwd).lastPathComponent.isEmpty
            ? "AI Memory" : URL(fileURLWithPath: cwd).lastPathComponent
        try execute(
            database,
            """
            INSERT INTO project (id, worktree, vcs, name, time_created, time_updated, sandboxes)
            VALUES (?, ?, 'git', ?, ?, ?, '[]');
            """,
            bindings: [
                .text(projectID), .text(cwd), .text(name),
                .integer(timestamp), .integer(timestamp),
            ]
        )
        return projectID
    }

    private static func insertOpenCodePart(
        _ database: OpaquePointer,
        messageID: String,
        sessionID: String,
        timestamp: Int64,
        data: [String: Any]
    ) throws {
        try execute(
            database,
            """
            INSERT INTO part (id, message_id, session_id, time_created, time_updated, data)
            VALUES (?, ?, ?, ?, ?, ?);
            """,
            bindings: [
                .text(compactID("part")), .text(messageID), .text(sessionID),
                .integer(timestamp), .integer(timestamp),
                .text(try jsonString(data)),
            ]
        )
    }

    // MARK: - General helpers

    private func normalizedProjectDirectory(_ path: String) -> String {
        let trimmed = path.trimmingCharacters(in: .whitespacesAndNewlines)
        if trimmed.isEmpty || trimmed == "." { return home.path }
        return trimmed
    }

    private static func claudePathCharacter(_ character: Character) -> Character {
        if "/\\:<>\"|?*".contains(character)
            || character.unicodeScalars.contains(where: {
                CharacterSet.controlCharacters.contains($0)
            }) {
            return "-"
        }
        return character
    }

    private static func title(_ conversation: ConversationDetail, firstUser: String) -> String {
        let candidate = conversation.summary?.trimmingCharacters(in: .whitespacesAndNewlines)
        let value = (candidate?.isEmpty == false ? candidate : nil)
            ?? (firstUser.isEmpty
                ? conversation.messages.first(where: { $0.role.lowercased() == "user" })?.content
                : firstUser)
            ?? "AI Memory imported conversation"
        return String(value.prefix(80))
    }

    private static func slug(_ value: String) -> String {
        let scalars = value.lowercased().unicodeScalars.map { scalar -> Character in
            CharacterSet.alphanumerics.contains(scalar) ? Character(String(scalar)) : "-"
        }
        let collapsed = String(scalars)
            .split(separator: "-", omittingEmptySubsequences: true)
            .joined(separator: "-")
        return collapsed.isEmpty ? "aimemory-import" : collapsed
    }

    private static func compactID(_ prefix: String) -> String {
        "\(prefix)_\(UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased())"
    }

    private static func date(_ value: String) -> Date {
        ISO8601DateFormatter().date(from: value) ?? Date()
    }

    private static func milliseconds(_ value: String) -> Int64 {
        Int64(date(value).timeIntervalSince1970 * 1_000)
    }

    private static let rolloutDateFormatter: DateFormatter = {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = TimeZone(secondsFromGMT: 0)
        formatter.dateFormat = "yyyy-MM-dd'T'HH-mm-ss"
        return formatter
    }()

    private static func metadataString(
        _ metadata: [String: JSONValue]?,
        key: String
    ) -> String? {
        guard case .string(let value) = metadata?[key] else { return nil }
        return value
    }

    private static func foundationValue(_ value: JSONValue) -> Any {
        switch value {
        case .null: NSNull()
        case .bool(let value): value
        case .number(let value): value
        case .string(let value): value
        case .array(let values): values.map(foundationValue)
        case .object(let values): values.mapValues(foundationValue)
        }
    }

    private static func jsonString(_ value: Any) throws -> String {
        let data = try JSONSerialization.data(withJSONObject: value, options: [.sortedKeys])
        return String(decoding: data, as: UTF8.self)
    }

    private static func writeJSONLines(_ values: [[String: Any]], to url: URL) throws {
        let body = try values
            .map { try jsonString($0) }
            .joined(separator: "\n") + "\n"
        try Data(body.utf8).write(to: url, options: [.atomic])
    }

    private static func writeJSONObject(_ value: [String: Any], to url: URL) throws {
        let data = try JSONSerialization.data(
            withJSONObject: value,
            options: [.prettyPrinted, .sortedKeys]
        )
        try data.write(to: url, options: [.atomic])
    }
}
