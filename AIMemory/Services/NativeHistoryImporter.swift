// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
import Foundation
import SQLite3

actor NativeHistoryImporter {
    private let store: NativeConversationStore
    private let home: URL

    init(
        store: NativeConversationStore = NativeConversationStore(),
        home: URL = FileManager.default.homeDirectoryForCurrentUser
    ) {
        self.store = store
        self.home = home
    }

    func importAll() async -> NativeHistoryImportReport {
        var imported: [String: Int] = [:]
        var warnings: [String] = []
        do {
            imported["codex"] = try await importCodex(
                databaseURL: home.appendingPathComponent(".codex/state_5.sqlite")
            )
        } catch {
            warnings.append("Codex：\(error.localizedDescription)")
        }
        do {
            imported["claude"] = try await importClaude(
                projectsURL: home.appendingPathComponent(".claude/projects")
            )
        } catch {
            warnings.append("Claude：\(error.localizedDescription)")
        }
        do {
            imported["gemini"] = try await importGemini(
                tmpURL: home.appendingPathComponent(".gemini/tmp")
            )
        } catch {
            warnings.append("Gemini：\(error.localizedDescription)")
        }
        do {
            let applicationSupport = FileManager.default.urls(
                for: .applicationSupportDirectory,
                in: .userDomainMask
            ).first?.appendingPathComponent("hermes/state.db")
            let fallback = home.appendingPathComponent(".hermes/state.db")
            let database = applicationSupport.flatMap {
                FileManager.default.fileExists(atPath: $0.path) ? $0 : nil
            } ?? fallback
            imported["hermes"] = try await importHermes(databaseURL: database)
        } catch {
            warnings.append("Hermes：\(error.localizedDescription)")
        }
        let additional = await NativeAdditionalHistoryImporter(
            store: store,
            home: home
        ).importAll()
        imported.merge(additional.imported) { _, new in new }
        warnings.append(contentsOf: additional.warnings)
        return NativeHistoryImportReport(imported: imported, warnings: warnings)
    }

    /// Re-index every supported local history store, then detect the sources
    /// that are actually present. This is the single launch-time entry point:
    /// callers never need to perform a separate manual scan before loading UI
    /// data, and repeated launches remain safe because every importer upserts
    /// stable source conversation identifiers.
    func synchronizeInstalledHistory() async throws -> NativeInstalledHistorySyncReport {
        let report = await importAll()
        let availableAgents = try await store.detectSources()
            .filter(\.available)
            .map(\.agent)
        return NativeInstalledHistorySyncReport(
            imported: report.imported,
            warnings: report.warnings,
            availableAgents: availableAgents
        )
    }

    func importAgent(_ agent: AgentKind) async -> NativeHistoryImportReport {
        var imported: [String: Int] = [:]
        var warnings: [String] = []
        do {
            switch agent {
            case .codex:
                imported[agent.rawValue] = try await importCodex(
                    databaseURL: home.appendingPathComponent(".codex/state_5.sqlite")
                )
            case .claude:
                imported[agent.rawValue] = try await importClaude(
                    projectsURL: home.appendingPathComponent(".claude/projects")
                )
            case .gemini:
                imported[agent.rawValue] = try await importGemini(
                    tmpURL: home.appendingPathComponent(".gemini/tmp")
                )
            case .hermes:
                let applicationSupport = FileManager.default.urls(
                    for: .applicationSupportDirectory,
                    in: .userDomainMask
                ).first?.appendingPathComponent("hermes/state.db")
                let fallback = home.appendingPathComponent(".hermes/state.db")
                let database = applicationSupport.flatMap {
                    FileManager.default.fileExists(atPath: $0.path) ? $0 : nil
                } ?? fallback
                imported[agent.rawValue] = try await importHermes(databaseURL: database)
            case .kimi, .antigravity, .opencode, .zcode:
                return await NativeAdditionalHistoryImporter(
                    store: store,
                    home: home
                ).importAgent(agent)
            }
        } catch {
            warnings.append("\(agent.label)：\(error.localizedDescription)")
        }
        return NativeHistoryImportReport(imported: imported, warnings: warnings)
    }

    func scan(repoRoot: String) async -> NativeHistoryImportReport {
        let report = await importAll()
        return NativeHistoryImportReport(
            imported: report.imported,
            warnings: report.warnings,
            requestedRepoRoot: repoRoot
        )
    }

    private func importCodex(databaseURL: URL) async throws -> Int {
        guard FileManager.default.fileExists(atPath: databaseURL.path) else { return 0 }
        let rows = try sqliteRows(
            databaseURL: databaseURL,
            sql: """
            SELECT id, rollout_path, cwd, title, created_at, updated_at
            FROM threads
            WHERE source IS NULL OR substr(ltrim(source), 1, 12) != '{"subagent":'
            ORDER BY updated_at DESC;
            """
        )
        var count = 0
        for row in rows {
            guard let id = row["id"] as? String,
                  let rolloutPath = row["rollout_path"] as? String,
                  FileManager.default.fileExists(atPath: rolloutPath) else { continue }
            do {
                let detail = try parseCodexRollout(
                    url: URL(fileURLWithPath: rolloutPath),
                    id: id,
                    projectDir: row["cwd"] as? String ?? "",
                    title: row["title"] as? String,
                    createdAt: Self.isoFromEpoch(row["created_at"] as? Int ?? 0),
                    updatedAt: Self.isoFromEpoch(row["updated_at"] as? Int ?? 0)
                )
                guard !detail.messages.isEmpty else { continue }
                try await store.upsertConversation(detail)
                count += 1
            } catch {
                continue
            }
        }
        return count
    }

    private func parseCodexRollout(
        url: URL,
        id: String,
        projectDir: String,
        title: String?,
        createdAt: String,
        updatedAt: String
    ) throws -> ConversationDetail {
        let lines = try String(contentsOf: url, encoding: .utf8)
            .split(whereSeparator: \.isNewline)
        var messages: [ConversationMessage] = []
        var files: [FileChange] = []
        var pending: [String: (name: String, input: JSONValue)] = [:]
        var firstTimestamp: String?
        var lastTimestamp: String?
        var rolloutCWD = projectDir

        for line in lines {
            guard let data = String(line).data(using: .utf8),
                  let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let payload = root["payload"] as? [String: Any] else { continue }
            let timestamp = root["timestamp"] as? String ?? updatedAt
            firstTimestamp = min(firstTimestamp ?? timestamp, timestamp)
            lastTimestamp = max(lastTimestamp ?? timestamp, timestamp)
            if root["type"] as? String == "session_meta",
               let cwd = payload["cwd"] as? String, !cwd.isEmpty {
                rolloutCWD = cwd
            }
            let payloadType = payload["type"] as? String ?? ""
            switch (root["type"] as? String, payloadType) {
            case ("event_msg", "user_message"):
                guard let text = payload["message"] as? String,
                      Self.meaningfulUserText(text) else { continue }
                messages.append(Self.message(role: "user", content: text, timestamp: timestamp))
            case ("event_msg", "agent_message"):
                guard let text = payload["message"] as? String,
                      !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { continue }
                if messages.last?.role == "assistant",
                   messages.last?.content == text {
                    continue
                }
                messages.append(Self.message(role: "assistant", content: text, timestamp: timestamp))
            case ("response_item", "function_call"):
                let callID = payload["call_id"] as? String ?? UUID().uuidString
                let name = payload["name"] as? String ?? "tool"
                let arguments = Self.jsonValue(
                    fromJSONString: payload["arguments"] as? String ?? "{}"
                )
                pending[callID] = (name, arguments)
            case ("response_item", "custom_tool_call"):
                let callID = payload["call_id"] as? String ?? UUID().uuidString
                let name = payload["name"] as? String ?? "tool"
                pending[callID] = (
                    name,
                    .string(payload["input"] as? String ?? "")
                )
            case ("response_item", "function_call_output"),
                 ("response_item", "custom_tool_call_output"):
                let callID = payload["call_id"] as? String ?? ""
                guard let call = pending.removeValue(forKey: callID) else { continue }
                let output = payload["output"] as? String
                let tool = ToolCall(
                    id: callID.isEmpty ? UUID().uuidString : callID,
                    name: call.name,
                    input: call.input,
                    output: output,
                    status: Self.outputLooksFailed(output) ? "error" : "success"
                )
                if let last = messages.indices.last,
                   messages[last].role == "assistant",
                   messages[last].timestamp == timestamp {
                    messages[last] = Self.appending(tool: tool, to: messages[last])
                } else {
                    messages.append(
                        ConversationMessage(
                            id: UUID().uuidString,
                            timestamp: timestamp,
                            role: "assistant",
                            content: "",
                            toolCalls: [tool],
                            metadata: [:]
                        )
                    )
                }
                files.append(contentsOf: Self.fileChanges(
                    output: output,
                    timestamp: timestamp,
                    messageID: messages.last?.id
                ))
            default:
                continue
            }
        }
        let effectiveTitle = Self.usefulTitle(title)
            ?? messages.first(where: { $0.role == "user" })?.content
        return ConversationDetail(
            id: id,
            sourceAgent: "codex",
            projectDir: rolloutCWD,
            createdAt: firstTimestamp ?? createdAt,
            updatedAt: lastTimestamp ?? updatedAt,
            summary: effectiveTitle.map { String($0.prefix(100)) },
            storagePath: url.path,
            resumeCommand: "codex resume \(id)",
            messages: messages,
            fileChanges: files
        )
    }

    private func importClaude(projectsURL: URL) async throws -> Int {
        let urls = claudeSessionURLs(projectsURL: projectsURL)
        var count = 0
        for url in urls {
            do {
                let detail = try parseClaudeSession(url: url)
                guard !detail.messages.isEmpty else { continue }
                try await store.upsertConversation(detail)
                count += 1
            } catch {
                continue
            }
        }
        return count
    }

    private func claudeSessionURLs(projectsURL: URL) -> [URL] {
        guard FileManager.default.fileExists(atPath: projectsURL.path),
              let enumerator = FileManager.default.enumerator(
                at: projectsURL,
                includingPropertiesForKeys: [.isRegularFileKey],
                options: [.skipsHiddenFiles]
              ) else { return [] }
        var urls: [URL] = []
        while let url = enumerator.nextObject() as? URL {
            guard url.pathExtension.lowercased() == "jsonl",
                  url.deletingLastPathComponent().deletingLastPathComponent()
                    .standardizedFileURL == projectsURL.standardizedFileURL
            else { continue }
            urls.append(url)
        }
        return urls
    }

    private func parseClaudeSession(url: URL) throws -> ConversationDetail {
        let lines = try String(contentsOf: url, encoding: .utf8)
            .split(whereSeparator: \.isNewline)
        var messages: [ConversationMessage] = []
        var files: [FileChange] = []
        var pending: [String: ToolCall] = [:]
        var assistantIndex: [String: Int] = [:]
        var summary: String?
        var cwd: String?
        var firstTimestamp: String?
        var lastTimestamp: String?

        for line in lines {
            guard let data = String(line).data(using: .utf8),
                  let event = try? JSONSerialization.jsonObject(with: data) as? [String: Any]
            else { continue }
            if event["isSidechain"] as? Bool == true { continue }
            if cwd == nil, let value = event["cwd"] as? String, !value.isEmpty { cwd = value }
            let timestamp = event["timestamp"] as? String
                ?? ISO8601DateFormatter().string(from: Date())
            firstTimestamp = min(firstTimestamp ?? timestamp, timestamp)
            lastTimestamp = max(lastTimestamp ?? timestamp, timestamp)
            let type = event["type"] as? String ?? ""
            if type == "summary" {
                summary = event["summary"] as? String
                continue
            }
            guard let payload = event["message"] as? [String: Any],
                  let role = payload["role"] as? String else { continue }
            let content = payload["content"]
            if role == "user", let text = content as? String {
                guard Self.meaningfulUserText(text) else { continue }
                messages.append(Self.message(role: "user", content: text, timestamp: timestamp))
                continue
            }
            guard let blocks = content as? [[String: Any]] else { continue }
            if role == "assistant" {
                let apiID = payload["id"] as? String ?? event["uuid"] as? String
                    ?? UUID().uuidString
                let index: Int
                if let existing = assistantIndex[apiID] {
                    index = existing
                } else {
                    messages.append(Self.message(role: "assistant", content: "", timestamp: timestamp))
                    index = messages.count - 1
                    assistantIndex[apiID] = index
                }
                var message = messages[index]
                var texts = message.content.isEmpty ? [] : [message.content]
                var tools = message.toolCalls
                for block in blocks {
                    switch block["type"] as? String {
                    case "text":
                        if let text = block["text"] as? String, !text.isEmpty { texts.append(text) }
                    case "tool_use":
                        let toolID = block["id"] as? String ?? UUID().uuidString
                        let name = block["name"] as? String ?? "tool"
                        let input = Self.jsonValue(from: block["input"])
                        let tool = ToolCall(
                            id: toolID,
                            name: name,
                            input: input,
                            output: nil,
                            status: "success"
                        )
                        pending[toolID] = tool
                        tools.append(tool)
                        if ["Write", "Edit", "NotebookEdit"].contains(name),
                           case .object(let object) = input,
                           case .string(let path)? = object["file_path"] ?? object["notebook_path"] {
                            files.append(FileChange(
                                path: path,
                                changeType: name == "Write" ? "created" : "modified",
                                timestamp: timestamp,
                                messageId: message.id
                            ))
                        }
                    default:
                        continue
                    }
                }
                message = ConversationMessage(
                    id: message.id,
                    timestamp: message.timestamp,
                    role: "assistant",
                    content: texts.joined(separator: "\n\n"),
                    toolCalls: tools,
                    metadata: [:]
                )
                messages[index] = message
            } else if role == "user" {
                for block in blocks where block["type"] as? String == "tool_result" {
                    guard let toolID = block["tool_use_id"] as? String,
                          let pendingTool = pending.removeValue(forKey: toolID)
                    else { continue }
                    let output = Self.stringContent(block["content"])
                    for index in messages.indices.reversed() {
                        guard messages[index].toolCalls.contains(where: { $0.id == toolID })
                        else { continue }
                        let updated = messages[index].toolCalls.map { tool in
                            tool.id == toolID
                                ? ToolCall(
                                    id: tool.id,
                                    name: pendingTool.name,
                                    input: pendingTool.input,
                                    output: output,
                                    status: Self.outputLooksFailed(output) ? "error" : "success"
                                )
                                : tool
                        }
                        messages[index] = ConversationMessage(
                            id: messages[index].id,
                            timestamp: messages[index].timestamp,
                            role: messages[index].role,
                            content: messages[index].content,
                            toolCalls: updated,
                            metadata: messages[index].metadata
                        )
                        break
                    }
                }
            }
        }
        let encodedProject = url.deletingLastPathComponent().lastPathComponent
        let project = cwd ?? Self.decodeClaudeProject(encodedProject)
        let id = url.deletingPathExtension().lastPathComponent
        let title = summary
            ?? messages.first(where: { $0.role == "user" })?.content
        return ConversationDetail(
            id: id,
            sourceAgent: "claude",
            projectDir: project,
            createdAt: firstTimestamp ?? "",
            updatedAt: lastTimestamp ?? "",
            summary: title.map { String($0.prefix(100)) },
            storagePath: url.path,
            resumeCommand: "claude --resume \(id)",
            messages: messages.filter { !$0.content.isEmpty || !$0.toolCalls.isEmpty },
            fileChanges: files
        )
    }

    private func importGemini(tmpURL: URL) async throws -> Int {
        guard FileManager.default.fileExists(atPath: tmpURL.path),
              let enumerator = FileManager.default.enumerator(
                at: tmpURL,
                includingPropertiesForKeys: [.isRegularFileKey],
                options: [.skipsHiddenFiles]
              ) else { return 0 }
        var urls: [URL] = []
        while let url = enumerator.nextObject() as? URL {
            guard url.pathExtension.lowercased() == "json",
                  url.deletingLastPathComponent().lastPathComponent == "chats"
            else { continue }
            urls.append(url)
        }
        var count = 0
        for url in urls {
            do {
                let data = try Data(contentsOf: url)
                guard let root = try JSONSerialization.jsonObject(with: data) as? [String: Any],
                      let id = root["sessionId"] as? String else { continue }
                let projectHash = root["projectHash"] as? String
                    ?? url.deletingLastPathComponent().deletingLastPathComponent().lastPathComponent
                let project = root["projectPath"] as? String
                    ?? root["cwd"] as? String
                    ?? "gemini:\(projectHash)"
                let createdAt = root["startTime"] as? String ?? ""
                let updatedAt = root["lastUpdated"] as? String ?? createdAt
                let messageObjects = root["messages"] as? [[String: Any]] ?? []
                var messages: [ConversationMessage] = []
                var changes: [FileChange] = []
                for object in messageObjects {
                    let type = object["type"] as? String ?? ""
                    guard type == "user" || type == "gemini" else { continue }
                    let role = type == "user" ? "user" : "assistant"
                    let content = object["content"] as? String ?? ""
                    if role == "user", !Self.meaningfulUserText(content) { continue }
                    let timestamp = object["timestamp"] as? String ?? createdAt
                    let messageID = object["id"] as? String ?? UUID().uuidString
                    var tools: [ToolCall] = []
                    for toolObject in object["toolCalls"] as? [[String: Any]] ?? [] {
                        let name = toolObject["name"] as? String ?? "tool"
                        let input = Self.jsonValue(from: toolObject["args"])
                        let output = toolObject["resultDisplay"] as? String
                            ?? Self.geminiToolOutput(toolObject)
                        let status = (toolObject["status"] as? String)?.lowercased()
                        tools.append(
                            ToolCall(
                                id: toolObject["id"] as? String ?? UUID().uuidString,
                                name: name,
                                input: input,
                                output: output,
                                status: status == "error" ? "error" : "success"
                            )
                        )
                        if let path = Self.filePath(tool: name, input: input) {
                            changes.append(
                                FileChange(
                                    path: path,
                                    changeType: name.lowercased().contains("create")
                                        ? "created" : "modified",
                                    timestamp: timestamp,
                                    messageId: messageID
                                )
                            )
                        }
                    }
                    guard !content.isEmpty || !tools.isEmpty else { continue }
                    messages.append(
                        ConversationMessage(
                            id: messageID,
                            timestamp: timestamp,
                            role: role,
                            content: content,
                            toolCalls: tools,
                            metadata: [:]
                        )
                    )
                }
                guard !messages.isEmpty else { continue }
                let summary = root["summary"] as? String
                    ?? messages.first(where: { $0.role == "user" })?.content
                try await store.upsertConversation(
                    ConversationDetail(
                        id: id,
                        sourceAgent: "gemini",
                        projectDir: project,
                        createdAt: createdAt,
                        updatedAt: updatedAt,
                        summary: summary.map { String($0.prefix(100)) },
                        storagePath: url.path,
                        resumeCommand: "gemini --resume \(id)",
                        messages: messages,
                        fileChanges: changes
                    )
                )
                count += 1
            } catch {
                continue
            }
        }
        return count
    }

    private func importHermes(databaseURL: URL) async throws -> Int {
        guard FileManager.default.fileExists(atPath: databaseURL.path) else { return 0 }
        let sessions = try sqliteRows(
            databaseURL: databaseURL,
            sql: """
            SELECT id, title, started_at, ended_at, cwd
            FROM sessions
            WHERE archived = 0
            ORDER BY started_at DESC;
            """
        )
        var count = 0
        for session in sessions {
            guard let id = session["id"] as? String else { continue }
            do {
                let rows = try sqliteRows(
                    databaseURL: databaseURL,
                    sql: """
                    SELECT id, role, content, tool_calls, tool_name, timestamp
                    FROM messages
                    WHERE session_id = '\(Self.sqlLiteral(id))' AND active = 1
                    ORDER BY timestamp ASC;
                    """
                )
                var messages: [ConversationMessage] = []
                for row in rows {
                    let role = row["role"] as? String ?? "assistant"
                    let timestamp = Self.isoFromEpochDouble(
                        row["timestamp"] as? Double ?? 0
                    )
                    if role == "tool" {
                        let name = row["tool_name"] as? String ?? ""
                        let output = row["content"] as? String
                        if let index = messages.indices.last,
                           messages[index].role == "assistant" {
                            var matched = false
                            let tools = messages[index].toolCalls.reversed().map { tool -> ToolCall in
                                guard !matched, tool.output == nil, tool.name == name else {
                                    return tool
                                }
                                matched = true
                                return ToolCall(
                                    id: tool.id,
                                    name: tool.name,
                                    input: tool.input,
                                    output: output,
                                    status: Self.outputLooksFailed(output) ? "error" : "success"
                                )
                            }.reversed()
                            messages[index] = ConversationMessage(
                                id: messages[index].id,
                                timestamp: messages[index].timestamp,
                                role: messages[index].role,
                                content: messages[index].content,
                                toolCalls: Array(tools),
                                metadata: messages[index].metadata
                            )
                        }
                        continue
                    }
                    var tools: [ToolCall] = []
                    if let text = row["tool_calls"] as? String,
                       let data = text.data(using: .utf8),
                       let array = try? JSONSerialization.jsonObject(with: data) as? [[String: Any]] {
                        for object in array {
                            let function = object["function"] as? [String: Any] ?? [:]
                            tools.append(
                                ToolCall(
                                    id: object["id"] as? String ?? UUID().uuidString,
                                    name: function["name"] as? String ?? "tool",
                                    input: Self.jsonValue(
                                        fromJSONString: function["arguments"] as? String ?? "{}"
                                    ),
                                    output: nil,
                                    status: "success"
                                )
                            )
                        }
                    }
                    let content = row["content"] as? String ?? ""
                    guard !content.isEmpty || !tools.isEmpty else { continue }
                    messages.append(
                        ConversationMessage(
                            id: String(row["id"] as? Int ?? messages.count),
                            timestamp: timestamp,
                            role: ["user", "assistant", "system"].contains(role)
                                ? role : "assistant",
                            content: content,
                            toolCalls: tools,
                            metadata: [:]
                        )
                    )
                }
                guard !messages.isEmpty else { continue }
                let started = session["started_at"] as? Double ?? 0
                let ended = session["ended_at"] as? Double ?? started
                try await store.upsertConversation(
                    ConversationDetail(
                        id: id,
                        sourceAgent: "hermes",
                        projectDir: session["cwd"] as? String ?? "",
                        createdAt: Self.isoFromEpochDouble(started),
                        updatedAt: Self.isoFromEpochDouble(ended),
                        summary: session["title"] as? String,
                        storagePath: databaseURL.path,
                        resumeCommand: "hermes resume \(id)",
                        messages: messages,
                        fileChanges: []
                    )
                )
                count += 1
            } catch {
                continue
            }
        }
        return count
    }

    private func sqliteRows(databaseURL: URL, sql: String) throws -> [[String: Any]] {
        var database: OpaquePointer?
        guard sqlite3_open_v2(
            databaseURL.path,
            &database,
            SQLITE_OPEN_READONLY | SQLITE_OPEN_FULLMUTEX,
            nil
        ) == SQLITE_OK, let database else {
            throw NativeHistoryImportError.sqlite
        }
        defer { sqlite3_close(database) }
        var statement: OpaquePointer?
        guard sqlite3_prepare_v2(database, sql, -1, &statement, nil) == SQLITE_OK,
              let statement else { throw NativeHistoryImportError.sqlite }
        defer { sqlite3_finalize(statement) }
        var rows: [[String: Any]] = []
        while sqlite3_step(statement) == SQLITE_ROW {
            var row: [String: Any] = [:]
            for index in 0..<sqlite3_column_count(statement) {
                let key = String(cString: sqlite3_column_name(statement, index))
                switch sqlite3_column_type(statement, index) {
                case SQLITE_INTEGER:
                    row[key] = Int(sqlite3_column_int64(statement, index))
                case SQLITE_FLOAT:
                    row[key] = sqlite3_column_double(statement, index)
                case SQLITE_TEXT:
                    if let text = sqlite3_column_text(statement, index) {
                        row[key] = String(cString: text)
                    }
                default:
                    break
                }
            }
            rows.append(row)
        }
        return rows
    }

    private static func message(
        role: String,
        content: String,
        timestamp: String
    ) -> ConversationMessage {
        ConversationMessage(
            id: UUID().uuidString,
            timestamp: timestamp,
            role: role,
            content: content,
            toolCalls: [],
            metadata: [:]
        )
    }

    private static func appending(
        tool: ToolCall,
        to message: ConversationMessage
    ) -> ConversationMessage {
        ConversationMessage(
            id: message.id,
            timestamp: message.timestamp,
            role: message.role,
            content: message.content,
            toolCalls: message.toolCalls + [tool],
            metadata: message.metadata
        )
    }

    private static func jsonValue(fromJSONString string: String) -> JSONValue {
        guard let data = string.data(using: .utf8),
              let object = try? JSONSerialization.jsonObject(with: data)
        else { return .string(string) }
        return jsonValue(from: object)
    }

    private static func jsonValue(from object: Any?) -> JSONValue {
        switch object {
        case nil, is NSNull: .null
        case let value as Bool: .bool(value)
        case let value as NSNumber: .number(value.doubleValue)
        case let value as String: .string(value)
        case let value as [Any]: .array(value.map { jsonValue(from: $0) })
        case let value as [String: Any]:
            .object(value.mapValues { jsonValue(from: $0) })
        default: .string(String(describing: object))
        }
    }

    private static func stringContent(_ value: Any?) -> String? {
        if let value = value as? String { return value }
        guard let value else { return nil }
        if let data = try? JSONSerialization.data(withJSONObject: value),
           let string = String(data: data, encoding: .utf8) { return string }
        return String(describing: value)
    }

    private static func fileChanges(
        output: String?,
        timestamp: String,
        messageID: String?
    ) -> [FileChange] {
        guard let output else { return [] }
        let text: String
        if let data = output.data(using: .utf8),
           let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
           let nested = object["output"] as? String {
            text = nested
        } else {
            text = output
        }
        guard let marker = text.range(of: "Updated the following files:") else { return [] }
        return text[marker.upperBound...].split(whereSeparator: \.isNewline).compactMap { line in
            let parts = line.trimmingCharacters(in: .whitespaces).split(
                separator: " ",
                maxSplits: 1
            )
            guard parts.count == 2 else { return nil }
            let type: String
            switch parts[0] {
            case "A": type = "created"
            case "D": type = "deleted"
            default: type = "modified"
            }
            return FileChange(
                path: String(parts[1]),
                changeType: type,
                timestamp: timestamp,
                messageId: messageID
            )
        }
    }

    private static func meaningfulUserText(_ text: String) -> Bool {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return false }
        return !trimmed.hasPrefix("<environment_context>")
            && !trimmed.hasPrefix("# AGENTS.md instructions")
    }

    private static func outputLooksFailed(_ output: String?) -> Bool {
        let text = output?.lowercased() ?? ""
        return text.contains("process exited with code 1")
            || text.contains("\"exit_code\":1")
            || text.contains("error:")
    }

    private static func usefulTitle(_ title: String?) -> String? {
        guard let title = title?.trimmingCharacters(in: .whitespacesAndNewlines),
              !title.isEmpty, title.lowercased() != "new thread" else { return nil }
        return title
    }

    private static func decodeClaudeProject(_ encoded: String) -> String {
        if encoded.count >= 3 {
            let characters = Array(encoded)
            if characters[0].isLetter, characters[1] == "-", characters[2] == "-" {
                return "\(characters[0]):/" + String(characters.dropFirst(3))
                    .replacingOccurrences(of: "-", with: "/")
            }
        }
        return encoded.replacingOccurrences(of: "-", with: "/")
    }

    private static func isoFromEpoch(_ value: Int) -> String {
        ISO8601DateFormatter().string(from: Date(timeIntervalSince1970: TimeInterval(value)))
    }

    private static func isoFromEpochDouble(_ value: Double) -> String {
        ISO8601DateFormatter().string(from: Date(timeIntervalSince1970: value))
    }

    private static func sqlLiteral(_ value: String) -> String {
        value.replacingOccurrences(of: "'", with: "''")
    }

    private static func geminiToolOutput(_ object: [String: Any]) -> String? {
        guard let results = object["result"] as? [[String: Any]] else { return nil }
        for result in results {
            guard let response = result["functionResponse"] as? [String: Any],
                  let value = response["response"] else { continue }
            if let object = value as? [String: Any] {
                if let output = object["output"] { return stringContent(output) }
                if let error = object["error"] { return stringContent(error) }
            }
            return stringContent(value)
        }
        return nil
    }

    private static func filePath(tool: String, input: JSONValue) -> String? {
        let lower = tool.lowercased()
        guard lower.contains("write") || lower.contains("edit") || lower.contains("create"),
              case .object(let object) = input else { return nil }
        for key in ["file_path", "path", "filePath", "filename"] {
            if case .string(let path)? = object[key] { return path }
        }
        return nil
    }
}

struct NativeHistoryImportReport: Sendable {
    let imported: [String: Int]
    let warnings: [String]
    let requestedRepoRoot: String?

    init(
        imported: [String: Int],
        warnings: [String],
        requestedRepoRoot: String? = nil
    ) {
        self.imported = imported
        self.warnings = warnings
        self.requestedRepoRoot = requestedRepoRoot
    }

    var total: Int { imported.values.reduce(0, +) }
}

/// Typed result of the automatic launch scan. Strings keep the actor boundary
/// independent of SwiftUI model isolation while preserving AgentKind ordering.
struct NativeInstalledHistorySyncReport: Sendable {
    let imported: [String: Int]
    let warnings: [String]
    let availableAgents: [String]

    var total: Int { imported.values.reduce(0, +) }
}

enum NativeHistoryImportError: LocalizedError {
    case sqlite

    var errorDescription: String? { "无法读取 agent 历史数据库。" }
}
