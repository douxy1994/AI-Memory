import Foundation
import SQLite3

/// Read-only importers for the remaining ChatMem-supported local agents.
/// Every parsed conversation is copied into AI Memory's independent index;
/// no source history file or source database is modified.
actor NativeAdditionalHistoryImporter {
    private let store: NativeConversationStore
    private let home: URL
    private let fileManager = FileManager.default

    init(store: NativeConversationStore, home: URL) {
        self.store = store
        self.home = home
    }

    func importAll() async -> NativeHistoryImportReport {
        var imported: [String: Int] = [:]
        var warnings: [String] = []
        await collect("kimi", into: &imported, warnings: &warnings) {
            try await self.importKimi(root: self.home.appendingPathComponent(".kimi-code"))
        }
        await collect("antigravity", into: &imported, warnings: &warnings) {
            try await self.importAntigravity(
                brain: self.home.appendingPathComponent(".gemini/antigravity/brain")
            )
        }
        await collect("opencode", into: &imported, warnings: &warnings) {
            try await self.importOpenCode()
        }
        await collect("zcode", into: &imported, warnings: &warnings) {
            try await self.importZCode(root: self.home.appendingPathComponent(".zcode"))
        }
        return NativeHistoryImportReport(imported: imported, warnings: warnings)
    }

    func importAgent(_ agent: AgentKind) async -> NativeHistoryImportReport {
        var imported: [String: Int] = [:]
        var warnings: [String] = []
        switch agent {
        case .kimi:
            await collect(agent.rawValue, into: &imported, warnings: &warnings) {
                try await self.importKimi(
                    root: self.home.appendingPathComponent(".kimi-code")
                )
            }
        case .antigravity:
            await collect(agent.rawValue, into: &imported, warnings: &warnings) {
                try await self.importAntigravity(
                    brain: self.home.appendingPathComponent(
                        ".gemini/antigravity/brain"
                    )
                )
            }
        case .opencode:
            await collect(agent.rawValue, into: &imported, warnings: &warnings) {
                try await self.importOpenCode()
            }
        case .zcode:
            await collect(agent.rawValue, into: &imported, warnings: &warnings) {
                try await self.importZCode(
                    root: self.home.appendingPathComponent(".zcode")
                )
            }
        case .claude, .codex, .gemini, .hermes:
            break
        }
        return NativeHistoryImportReport(imported: imported, warnings: warnings)
    }

    private func collect(
        _ name: String,
        into imported: inout [String: Int],
        warnings: inout [String],
        operation: () async throws -> Int
    ) async {
        do {
            imported[name] = try await operation()
        } catch {
            warnings.append("\(name)：\(error.localizedDescription)")
        }
    }

    // MARK: - Kimi Code

    private func importKimi(root: URL) async throws -> Int {
        let sessions = root.appendingPathComponent("sessions")
        guard fileManager.fileExists(atPath: sessions.path),
              let workspaces = try? fileManager.contentsOfDirectory(
                at: sessions,
                includingPropertiesForKeys: [.isDirectoryKey],
                options: [.skipsHiddenFiles]
              ) else { return 0 }
        var count = 0
        for workspace in workspaces.sorted(by: { $0.path < $1.path }) {
            guard let directories = try? fileManager.contentsOfDirectory(
                at: workspace,
                includingPropertiesForKeys: [.isDirectoryKey],
                options: [.skipsHiddenFiles]
            ) else { continue }
            for session in directories.sorted(by: { $0.path < $1.path }) {
                do {
                    if let detail = try parseKimiSession(session) {
                        try await store.upsertConversation(detail)
                        count += 1
                    }
                } catch {
                    continue
                }
            }
        }
        return count
    }

    private func parseKimiSession(_ session: URL) throws -> ConversationDetail? {
        let stateURL = session.appendingPathComponent("state.json")
        let state = (try? jsonObject(at: stateURL)) ?? [:]
        let agentsURL = session.appendingPathComponent("agents")
        guard let agents = try? fileManager.contentsOfDirectory(
            at: agentsURL,
            includingPropertiesForKeys: [.isDirectoryKey],
            options: [.skipsHiddenFiles]
        ) else { return nil }
        let orderedAgents = agents.sorted {
            if $0.lastPathComponent == "main" { return true }
            if $1.lastPathComponent == "main" { return false }
            return $0.lastPathComponent < $1.lastPathComponent
        }
        var messages: [ConversationMessage] = []
        var changes: [FileChange] = []
        var inferredProject: String?
        for agent in orderedAgents {
            let wire = agent.appendingPathComponent("wire.jsonl")
            guard fileManager.fileExists(atPath: wire.path) else { continue }
            let parsed = try parseKimiWire(wire, agent: agent.lastPathComponent)
            messages.append(contentsOf: parsed.messages)
            changes.append(contentsOf: parsed.changes)
            inferredProject = inferredProject ?? parsed.project
        }
        guard !messages.isEmpty else { return nil }
        messages.sort { $0.timestamp < $1.timestamp }
        let id = session.lastPathComponent
        let created = string(state["createdAt"]) ?? messages.first?.timestamp ?? ""
        let updated = string(state["updatedAt"]) ?? messages.last?.timestamp ?? created
        let summary = useful(string(state["title"]))
            ?? messages.first(where: { $0.role == "user" })?.content
        return ConversationDetail(
            id: id,
            sourceAgent: "kimi",
            projectDir: string(state["workDir"]) ?? inferredProject ?? "",
            createdAt: created,
            updatedAt: updated,
            summary: summary.map { String($0.prefix(100)) },
            storagePath: stateURL.path,
            resumeCommand: "kimi --session \(id)",
            messages: messages,
            fileChanges: changes
        )
    }

    private func parseKimiWire(
        _ url: URL,
        agent: String
    ) throws -> (messages: [ConversationMessage], changes: [FileChange], project: String?) {
        let text = try String(contentsOf: url, encoding: .utf8)
        var messages: [ConversationMessage] = []
        var changes: [FileChange] = []
        var pending: [String: (message: Int, tool: Int)] = [:]
        var cachedResults: [String: (String, Bool)] = [:]
        var currentStep: String?
        var project: String?
        var lastTimestamp = ""
        for rawLine in text.split(whereSeparator: \.isNewline) {
            guard let data = String(rawLine).data(using: .utf8),
                  let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any]
            else { continue }
            let timestamp = isoFromMilliseconds(number(root["time"])) ?? lastTimestamp
            lastTimestamp = timestamp
            switch string(root["type"]) {
            case "turn.prompt":
                let parts = root["input"] as? [[String: Any]] ?? []
                let content = parts
                    .filter { string($0["type"]) == "text" }
                    .compactMap { string($0["text"]) }
                    .joined(separator: "\n")
                    .trimmingCharacters(in: .whitespacesAndNewlines)
                guard !content.isEmpty else { continue }
                currentStep = nil
                messages.append(message(
                    role: "user",
                    content: content,
                    timestamp: timestamp,
                    metadata: ["kimi_agent": .string(agent)]
                ))
            case "context.append_loop_event":
                guard let event = root["event"] as? [String: Any] else { continue }
                let eventType = string(event["type"]) ?? ""
                let step = "\(string(event["turnId"]) ?? ""):\(integer(event["step"]) ?? 0)"
                if eventType == "content.part" {
                    guard let part = event["part"] as? [String: Any] else { continue }
                    let partType = string(part["type"]) ?? ""
                    let rawContent = partType == "think"
                        ? string(part["think"])
                        : string(part["text"])
                    let content = rawContent?
                        .trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
                    guard !content.isEmpty else { continue }
                    let index = ensureAssistant(
                        step: step,
                        timestamp: timestamp,
                        agent: agent,
                        currentStep: &currentStep,
                        messages: &messages
                    )
                    let old = messages[index]
                    var metadata = old.metadata ?? [:]
                    var visible = old.content
                    if partType == "think" {
                        let previous = metadata["thinking"]?.stringValue ?? ""
                        metadata["thinking"] = .string(
                            [previous, content].filter { !$0.isEmpty }.joined(separator: "\n\n")
                        )
                    } else {
                        visible = [visible, content].filter { !$0.isEmpty }.joined(separator: "\n\n")
                    }
                    messages[index] = ConversationMessage(
                        id: old.id,
                        timestamp: old.timestamp,
                        role: old.role,
                        content: visible,
                        toolCalls: old.toolCalls,
                        metadata: metadata
                    )
                } else if eventType == "tool.call" {
                    let index = ensureAssistant(
                        step: step,
                        timestamp: timestamp,
                        agent: agent,
                        currentStep: &currentStep,
                        messages: &messages
                    )
                    let name = string(event["name"]) ?? "tool"
                    let input = jsonValue(event["args"])
                    let callID = string(event["toolCallId"]) ?? UUID().uuidString
                    let cached = cachedResults[callID]
                    let tool = ToolCall(
                        id: callID,
                        name: name,
                        input: input,
                        output: cached?.0,
                        status: cached?.1 == true ? "error" : "success"
                    )
                    let old = messages[index]
                    messages[index] = ConversationMessage(
                        id: old.id,
                        timestamp: old.timestamp,
                        role: old.role,
                        content: old.content,
                        toolCalls: old.toolCalls + [tool],
                        metadata: old.metadata
                    )
                    pending[callID] = (index, old.toolCalls.count)
                    for pair in namedStrings(event["args"]) {
                        let path = normalizePath(pair.value)
                        if project == nil, isProjectKey(pair.key), isAbsolute(path) {
                            project = path
                        }
                        if isFileKey(pair.key), isAbsolute(path) {
                            changes.append(FileChange(
                                path: path,
                                changeType: name.lowercased().contains("create")
                                    ? "created" : "modified",
                                timestamp: timestamp,
                                messageId: old.id
                            ))
                        }
                    }
                } else if eventType == "tool.result" {
                    guard let callID = string(event["toolCallId"]), !callID.isEmpty else { continue }
                    let result = event["result"] as? [String: Any] ?? [:]
                    let outputObject = result["output"]
                    let output = string(outputObject) ?? serialized(outputObject)
                    let isError = bool(result["isError"]) ?? bool(result["is_error"]) ?? false
                    cachedResults[callID] = (output, isError)
                    if let location = pending[callID],
                       messages.indices.contains(location.message),
                       messages[location.message].toolCalls.indices.contains(location.tool) {
                        let oldMessage = messages[location.message]
                        var tools = oldMessage.toolCalls
                        let oldTool = tools[location.tool]
                        tools[location.tool] = ToolCall(
                            id: oldTool.id,
                            name: oldTool.name,
                            input: oldTool.input,
                            output: output,
                            status: isError ? "error" : "success"
                        )
                        messages[location.message] = ConversationMessage(
                            id: oldMessage.id,
                            timestamp: oldMessage.timestamp,
                            role: oldMessage.role,
                            content: oldMessage.content,
                            toolCalls: tools,
                            metadata: oldMessage.metadata
                        )
                    }
                }
            default:
                continue
            }
        }
        return (messages.filter { !$0.content.isEmpty || !$0.toolCalls.isEmpty }, changes, project)
    }

    private func ensureAssistant(
        step: String,
        timestamp: String,
        agent: String,
        currentStep: inout String?,
        messages: inout [ConversationMessage]
    ) -> Int {
        if currentStep != step {
            currentStep = step
            messages.append(message(
                role: "assistant",
                content: "",
                timestamp: timestamp,
                metadata: ["kimi_agent": .string(agent)]
            ))
        }
        return messages.count - 1
    }

    // MARK: - Google Antigravity

    private func importAntigravity(brain: URL) async throws -> Int {
        guard let sessions = try? fileManager.contentsOfDirectory(
            at: brain,
            includingPropertiesForKeys: [.isDirectoryKey],
            options: [.skipsHiddenFiles]
        ) else { return 0 }
        var count = 0
        for session in sessions.sorted(by: { $0.path < $1.path }) {
            let transcript = session
                .appendingPathComponent(".system_generated/logs/transcript.jsonl")
            guard fileManager.fileExists(atPath: transcript.path) else { continue }
            do {
                let detail = try parseAntigravity(transcript, id: session.lastPathComponent)
                guard !detail.messages.isEmpty else { continue }
                try await store.upsertConversation(detail)
                count += 1
            } catch {
                continue
            }
        }
        return count
    }

    private func parseAntigravity(_ url: URL, id: String) throws -> ConversationDetail {
        let text = try String(contentsOf: url, encoding: .utf8)
        var messages: [ConversationMessage] = []
        var changes: [FileChange] = []
        var project: String?
        for rawLine in text.split(whereSeparator: \.isNewline) {
            guard let data = String(rawLine).data(using: .utf8),
                  let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any]
            else { continue }
            let source = string(root["source"]) ?? ""
            let role: String
            switch source {
            case "USER_EXPLICIT", "USER": role = "user"
            case "MODEL": role = "assistant"
            case "SYSTEM": role = "system"
            default: role = "system"
            }
            var content = string(root["content"])?.trimmingCharacters(
                in: .whitespacesAndNewlines
            ) ?? ""
            if role == "user",
               let start = content.range(of: "<USER_REQUEST>"),
               let end = content.range(of: "</USER_REQUEST>", range: start.upperBound..<content.endIndex) {
                content = String(content[start.upperBound..<end.lowerBound])
                    .trimmingCharacters(in: .whitespacesAndNewlines)
            }
            let timestamp = string(root["created_at"]) ?? ""
            let messageID = UUID().uuidString
            var tools: [ToolCall] = []
            for rawTool in root["tool_calls"] as? [[String: Any]] ?? [] {
                let name = string(rawTool["name"]) ?? "tool"
                let input = jsonValue(rawTool["args"])
                tools.append(ToolCall(
                    name: name,
                    input: input,
                    output: nil,
                    status: (string(root["type"]) == "ERROR_MESSAGE"
                             || string(root["status"]) == "ERROR") ? "error" : "success"
                ))
                for pair in namedStrings(rawTool["args"]) {
                    let path = normalizePath(pair.value)
                    if project == nil, isProjectKey(pair.key), isAbsolute(path) {
                        project = path
                    }
                    if isFileKey(pair.key), isAbsolute(path) {
                        changes.append(FileChange(
                            path: path,
                            changeType: "modified",
                            timestamp: timestamp,
                            messageId: messageID
                        ))
                    }
                }
            }
            var metadata: [String: JSONValue] = [:]
            if !source.isEmpty { metadata["antigravity_source"] = .string(source) }
            if let thinking = useful(string(root["thinking"])) {
                metadata["thinking"] = .string(thinking)
            }
            guard !content.isEmpty || !tools.isEmpty || !metadata.isEmpty else { continue }
            messages.append(ConversationMessage(
                id: messageID,
                timestamp: timestamp,
                role: role,
                content: content,
                toolCalls: tools,
                metadata: metadata
            ))
        }
        return ConversationDetail(
            id: id,
            sourceAgent: "antigravity",
            projectDir: project ?? url.deletingLastPathComponent()
                .deletingLastPathComponent().deletingLastPathComponent().path,
            createdAt: messages.first?.timestamp ?? "",
            updatedAt: messages.last?.timestamp ?? "",
            summary: messages.first(where: { $0.role == "user" }).map {
                String($0.content.prefix(100))
            },
            storagePath: url.path,
            resumeCommand: nil,
            messages: messages,
            fileChanges: changes
        )
    }

    // MARK: - OpenCode

    private func importOpenCode() async throws -> Int {
        let base = home.appendingPathComponent(".local/share/opencode")
        let primary = base.appendingPathComponent("opencode.db")
        let database: URL?
        if fileManager.fileExists(atPath: primary.path) {
            database = primary
        } else {
            database = (try? fileManager.contentsOfDirectory(
                at: base,
                includingPropertiesForKeys: [.contentModificationDateKey],
                options: [.skipsHiddenFiles]
            ))?
                .filter {
                    $0.lastPathComponent.hasPrefix("opencode-")
                        && $0.pathExtension == "db"
                }
                .sorted {
                    let left = (try? $0.resourceValues(
                        forKeys: [.contentModificationDateKey]
                    ).contentModificationDate) ?? .distantPast
                    let right = (try? $1.resourceValues(
                        forKeys: [.contentModificationDateKey]
                    ).contentModificationDate) ?? .distantPast
                    return left > right
                }
                .first
        }
        guard let database else { return 0 }
        let sessions = try sqliteRows(
            database,
            """
            SELECT id, directory, title, time_created, time_updated
            FROM session WHERE time_archived IS NULL
            ORDER BY time_updated DESC;
            """
        )
        var count = 0
        for session in sessions {
            guard let id = string(session["id"]) else { continue }
            do {
                let detail = try parseOpenCode(database, session: session, id: id)
                guard !detail.messages.isEmpty else { continue }
                try await store.upsertConversation(detail)
                count += 1
            } catch {
                continue
            }
        }
        return count
    }

    private func parseOpenCode(
        _ database: URL,
        session: [String: Any],
        id: String
    ) throws -> ConversationDetail {
        let rows = try sqliteRows(
            database,
            """
            SELECT id, time_created, data FROM message
            WHERE session_id = '\(sqlLiteral(id))'
            ORDER BY time_created ASC, rowid ASC;
            """
        )
        var messages: [ConversationMessage] = []
        var changes: [FileChange] = []
        for row in rows {
            guard let sourceID = string(row["id"]) else { continue }
            let createdMS = integer(row["time_created"]) ?? 0
            let messageData = jsonObject(string(row["data"]) ?? "{}")
            let parts = try sqliteRows(
                database,
                """
                SELECT id, time_created, data FROM part
                WHERE session_id = '\(sqlLiteral(id))'
                  AND message_id = '\(sqlLiteral(sourceID))'
                ORDER BY time_created ASC, rowid ASC;
                """
            )
            var content: [String] = []
            var tools: [ToolCall] = []
            var reasoning: [String] = []
            let messageID = "opencode:\(id):\(sourceID)"
            for partRow in parts {
                let object = jsonObject(string(partRow["data"]) ?? "{}")
                switch string(object["type"]) {
                case "text":
                    if bool(object["ignored"]) != true, let text = useful(string(object["text"])) {
                        content.append(text)
                    }
                case "reasoning":
                    if let text = useful(string(object["text"])) { reasoning.append(text) }
                case "tool":
                    let state = object["state"] as? [String: Any] ?? [:]
                    let status = string(state["status"]) == "error" ? "error" : "success"
                    let output = string(state["output"]) ?? string(state["error"])
                        ?? string((state["metadata"] as? [String: Any])?["output"])
                    tools.append(ToolCall(
                        name: string(object["tool"]) ?? "tool",
                        input: jsonValue(state["input"]),
                        output: output,
                        status: status
                    ))
                case "patch":
                    let timestamp = isoFromMilliseconds(
                        number((object["time"] as? [String: Any])?["start"])
                            ?? number(partRow["time_created"])
                    ) ?? ""
                    for path in object["files"] as? [String] ?? [] {
                        changes.append(FileChange(
                            path: path,
                            changeType: "modified",
                            timestamp: timestamp,
                            messageId: messageID
                        ))
                    }
                case "file":
                    if let label = string(object["filename"]) ?? string(object["url"]) {
                        content.append("[file: \(label)]")
                    }
                default:
                    continue
                }
            }
            var metadata: [String: JSONValue] = [
                "opencode_message_id": .string(sourceID)
            ]
            if !reasoning.isEmpty {
                metadata["reasoning"] = .array(reasoning.map(JSONValue.string))
            }
            let role = ["assistant", "system"].contains(string(messageData["role"]) ?? "")
                ? string(messageData["role"])! : "user"
            let timestamp = isoFromMilliseconds(
                number((messageData["time"] as? [String: Any])?["created"])
                    ?? Double(createdMS)
            ) ?? ""
            guard !content.isEmpty || !tools.isEmpty else { continue }
            messages.append(ConversationMessage(
                id: messageID,
                timestamp: timestamp,
                role: role,
                content: content.joined(separator: "\n\n"),
                toolCalls: tools,
                metadata: metadata
            ))
        }
        let created = isoFromMilliseconds(number(session["time_created"])) ?? ""
        let updated = isoFromMilliseconds(number(session["time_updated"])) ?? created
        let title = useful(string(session["title"]))
            ?? messages.first(where: { $0.role == "user" })?.content
        return ConversationDetail(
            id: id,
            sourceAgent: "opencode",
            projectDir: string(session["directory"]) ?? "",
            createdAt: created,
            updatedAt: updated,
            summary: title.map { String($0.prefix(100)) },
            storagePath: database.path,
            resumeCommand: "opencode --session \(id)",
            messages: messages,
            fileChanges: changes
        )
    }

    // MARK: - ZCode v2 tasks

    private func importZCode(root: URL) async throws -> Int {
        let sessions = root.appendingPathComponent("v2/sessions")
        guard let profiles = try? fileManager.contentsOfDirectory(
            at: sessions,
            includingPropertiesForKeys: [.isDirectoryKey],
            options: [.skipsHiddenFiles]
        ) else { return 0 }
        var count = 0
        for profile in profiles.sorted(by: { $0.path < $1.path }) {
            guard let tasks = try? fileManager.contentsOfDirectory(
                at: profile,
                includingPropertiesForKeys: [.isRegularFileKey],
                options: [.skipsHiddenFiles]
            ) else { continue }
            for task in tasks.filter({ $0.pathExtension == "json" }).sorted(by: {
                $0.path < $1.path
            }) {
                do {
                    guard let detail = try parseZCodeTask(
                        task,
                        profile: profile.lastPathComponent
                    ) else { continue }
                    try await store.upsertConversation(detail)
                    count += 1
                } catch {
                    continue
                }
            }
        }
        return count
    }

    private func parseZCodeTask(
        _ url: URL,
        profile: String
    ) throws -> ConversationDetail? {
        let root = try jsonObject(at: url)
        let meta = root["meta"] as? [String: Any] ?? [:]
        let providerRaw = (string(meta["provider"]) ?? "unknown").lowercased()
        let provider = ["claude", "codex", "gemini", "opencode", "glm"]
            .contains(providerRaw) ? providerRaw : "unknown"
        let taskID = string(meta["taskId"]) ?? url.deletingPathExtension().lastPathComponent
        let id = "\(provider):task:\(profile):\(taskID)"
        let created = isoFromMilliseconds(number(meta["createdAt"])) ?? ""
        let updated = isoFromMilliseconds(number(meta["updatedAt"])) ?? created
        var messages: [ConversationMessage] = []
        for (index, object) in (root["messages"] as? [[String: Any]] ?? []).enumerated() {
            let rawRole = string(object["role"]) ?? "user"
            let role = ["assistant", "system"].contains(rawRole) ? rawRole : "user"
            var content = string(object["content"]) ?? ""
            if content.isEmpty {
                content = (object["parts"] as? [[String: Any]] ?? [])
                    .compactMap { part -> String? in
                        if let value = string(part["content"]) { return value }
                        return string((part["content"] as? [String: Any])?["text"])
                    }
                    .joined(separator: "\n")
            }
            var tools: [ToolCall] = []
            for (toolIndex, tool) in (object["tools"] as? [[String: Any]] ?? []).enumerated() {
                let raw = tool["raw"] as? [String: Any] ?? [:]
                let claude = (raw["_meta"] as? [String: Any])?["claudeCode"]
                    as? [String: Any]
                let name = string(tool["title"]) ?? string(tool["kind"])
                    ?? string(claude?["toolName"]) ?? "tool \(toolIndex + 1)"
                let status = ["completed", "success", "succeeded"]
                    .contains(string(tool["status"]) ?? "") ? "success" : "error"
                tools.append(ToolCall(
                    name: name,
                    input: jsonValue(tool["input"] ?? tool["raw"]),
                    output: string(tool["output"]) ?? string(raw["rawOutput"]),
                    status: status
                ))
            }
            let trimmed = content.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !trimmed.isEmpty || !tools.isEmpty else { continue }
            if role == "user", isControlText(trimmed) { continue }
            var metadata: [String: JSONValue] = [
                "zcode_engine": .string(provider),
                "zcode_cli": .string(provider),
                "zcode_profile": .string(profile),
                "zcode_task_id": .string(taskID),
                "zcode_storage_path": .string(url.path)
            ]
            if let model = string(object["model"]) ?? string(meta["model"]) {
                metadata["model"] = .string(model)
            }
            messages.append(ConversationMessage(
                id: "zcode-task:\(profile):\(taskID):\(index)",
                timestamp: isoFromMilliseconds(number(object["timestamp"])) ?? updated,
                role: role,
                content: content,
                toolCalls: tools,
                metadata: metadata
            ))
        }
        guard !messages.isEmpty else { return nil }
        var changes: [FileChange] = []
        let summary = meta["changeSummary"] as? [String: Any] ?? [:]
        for file in summary["files"] as? [[String: Any]] ?? [] {
            guard let path = string(file["path"]) else { continue }
            let added = integer(file["added"]) ?? 0
            let removed = integer(file["removed"]) ?? 0
            changes.append(FileChange(
                path: path,
                changeType: added > 0 && removed == 0 ? "created" : "modified",
                timestamp: updated,
                messageId: messages.last?.id
            ))
        }
        let title = useful(string(meta["title"]))
            ?? messages.first(where: { $0.role == "user" })?.content
        return ConversationDetail(
            id: id,
            sourceAgent: "zcode",
            projectDir: string(meta["workspacePath"]) ?? string(meta["cwd"]) ?? "",
            createdAt: created,
            updatedAt: updated,
            summary: title.map { String($0.prefix(100)) },
            storagePath: url.path,
            resumeCommand: nil,
            messages: messages,
            fileChanges: changes
        )
    }

    // MARK: - Shared decoding

    private func jsonObject(at url: URL) throws -> [String: Any] {
        let data = try Data(contentsOf: url, options: [.mappedIfSafe])
        return try JSONSerialization.jsonObject(with: data) as? [String: Any] ?? [:]
    }

    private func jsonObject(_ text: String) -> [String: Any] {
        guard let data = text.data(using: .utf8) else { return [:] }
        return (try? JSONSerialization.jsonObject(with: data) as? [String: Any]) ?? [:]
    }

    private func sqliteRows(_ url: URL, _ sql: String) throws -> [[String: Any]] {
        var database: OpaquePointer?
        guard sqlite3_open_v2(
            url.path,
            &database,
            SQLITE_OPEN_READONLY | SQLITE_OPEN_FULLMUTEX,
            nil
        ) == SQLITE_OK, let database else { throw AdditionalHistoryError.sqlite }
        defer { sqlite3_close(database) }
        var statement: OpaquePointer?
        guard sqlite3_prepare_v2(database, sql, -1, &statement, nil) == SQLITE_OK,
              let statement else { throw AdditionalHistoryError.sqlite }
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
                    continue
                }
            }
            rows.append(row)
        }
        return rows
    }

    private func message(
        role: String,
        content: String,
        timestamp: String,
        metadata: [String: JSONValue] = [:]
    ) -> ConversationMessage {
        ConversationMessage(
            id: UUID().uuidString,
            timestamp: timestamp,
            role: role,
            content: content,
            toolCalls: [],
            metadata: metadata
        )
    }

    private func jsonValue(_ object: Any?) -> JSONValue {
        switch object {
        case nil, is NSNull: return .null
        case let value as Bool: return .bool(value)
        case let value as NSNumber: return .number(value.doubleValue)
        case let value as String: return .string(value)
        case let value as [Any]: return .array(value.map(jsonValue))
        case let value as [String: Any]:
            return .object(value.mapValues(jsonValue))
        default:
            return .string(String(describing: object!))
        }
    }

    private func namedStrings(_ object: Any?) -> [(key: String, value: String)] {
        var result: [(String, String)] = []
        func visit(_ value: Any?, key: String?) {
            if let text = value as? String, let key {
                result.append((key, cleanEncoded(text)))
            } else if let dictionary = value as? [String: Any] {
                for (nestedKey, nested) in dictionary { visit(nested, key: nestedKey) }
            } else if let array = value as? [Any] {
                for nested in array { visit(nested, key: key) }
            }
        }
        visit(object, key: nil)
        return result
    }

    private func cleanEncoded(_ value: String) -> String {
        var current = value.trimmingCharacters(in: .whitespacesAndNewlines)
        for _ in 0..<2 {
            guard current.first == "\"", current.last == "\"",
                  let data = current.data(using: .utf8),
                  let decoded = try? JSONSerialization.jsonObject(with: data) as? String
            else { break }
            current = decoded.trimmingCharacters(in: .whitespacesAndNewlines)
        }
        return current
    }

    private func string(_ value: Any?) -> String? {
        if let string = value as? String { return string }
        return nil
    }

    private func number(_ value: Any?) -> Double? {
        if let number = value as? NSNumber { return number.doubleValue }
        if let string = value as? String { return Double(string) }
        return nil
    }

    private func integer(_ value: Any?) -> Int? {
        number(value).map(Int.init)
    }

    private func bool(_ value: Any?) -> Bool? {
        if let bool = value as? Bool { return bool }
        if let number = value as? NSNumber { return number.boolValue }
        return nil
    }

    private func useful(_ value: String?) -> String? {
        guard let trimmed = value?.trimmingCharacters(in: .whitespacesAndNewlines),
              !trimmed.isEmpty else { return nil }
        return trimmed
    }

    private func serialized(_ value: Any?) -> String {
        guard let value, JSONSerialization.isValidJSONObject(value),
              let data = try? JSONSerialization.data(withJSONObject: value),
              let string = String(data: data, encoding: .utf8) else {
            return value.map(String.init(describing:)) ?? ""
        }
        return string
    }

    private func isoFromMilliseconds(_ value: Double?) -> String? {
        guard let value, value > 0 else { return nil }
        let seconds = value > 10_000_000_000 ? value / 1_000 : value
        return ISO8601DateFormatter().string(from: Date(timeIntervalSince1970: seconds))
    }

    private func normalizePath(_ value: String) -> String {
        let cleaned = cleanEncoded(value)
        return cleaned.hasPrefix("file://") ? String(cleaned.dropFirst(7)) : cleaned
    }

    private func isAbsolute(_ value: String) -> Bool {
        value.hasPrefix("/") || value.hasPrefix("~/")
            || (value.count > 2 && value[value.index(after: value.startIndex)] == ":")
    }

    private func isProjectKey(_ value: String) -> Bool {
        ["cwd", "currentworkingdirectory", "workingdirectory", "workdir",
         "projectpath", "projectdir"].contains(value.lowercased())
    }

    private func isFileKey(_ value: String) -> Bool {
        ["absolutepath", "absolute_path", "filepath", "file_path", "path"]
            .contains(value.lowercased())
    }

    private func isControlText(_ value: String) -> Bool {
        let text = value.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        return text.isEmpty || text == "no response requested."
            || text.hasPrefix("<local-command-")
            || text.hasPrefix("<command-")
            || text.hasPrefix("<system-reminder")
    }

    private func sqlLiteral(_ value: String) -> String {
        value.replacingOccurrences(of: "'", with: "''")
    }
}

private enum AdditionalHistoryError: LocalizedError {
    case sqlite

    var errorDescription: String? {
        "无法只读打开历史数据库"
    }
}

private extension JSONValue {
    var stringValue: String? {
        if case .string(let value) = self { return value }
        return nil
    }
}
