// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
import Foundation

@main
struct AIMemoryMCPMain {
    static func main() async {
        do {
            var database: NativeDatabase? = try NativeDatabase()
            _ = try await database?.currentSchemaVersion()
            database = nil
            let server = NativeMCPServer()
            while let line = readLine() {
                guard !line.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
                    continue
                }
                if let response = await server.handle(line: line) {
                    FileHandle.standardOutput.write(response)
                    FileHandle.standardOutput.write(Data([0x0A]))
                }
            }
        } catch {
            let message = "[AI Memory MCP] \(error.localizedDescription)\n"
            FileHandle.standardError.write(Data(message.utf8))
        }
    }
}

private actor NativeMCPServer {
    private let store = NativeConversationStore()
    private let integrations = NativeAgentIntegrationStore()
    private let history = NativeHistoryImporter()
    private let encoder: JSONEncoder = {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        return encoder
    }()

    private var toolDefinitions: [[String: Any]] { [
        Self.tool("get_repo_memory", "Return compact approved startup rules for an agent",
             required: ["repo_root"], optional: ["task_hint"]),
        Self.tool("get_project_context", "Return approved rules, recent handoff, diagnostics, and compact local-history evidence",
             required: ["repo_root", "query"], optional: ["intent", "limit"]),
        Self.tool("get_repo_memory_health", "Return local-history and memory diagnostics",
             required: ["repo_root"]),
        Self.tool("import_all_local_history", "Import supported local agent histories into AI Memory's independent index"),
        Self.tool("scan_repo_conversations", "Scan and return repository memory health",
             required: ["repo_root"]),
        Self.tool("merge_repo_alias", "Link an older project path alias to this repository",
             required: ["repo_root", "alias_root"]),
        Self.tool("search_repo_history", "Search indexed local repository history",
             required: ["repo_root", "query"], optional: ["limit"]),
        Self.tool("read_history_conversation", "Read an indexed local conversation",
             required: ["repo_root", "conversation_id"],
             optional: ["message_id", "query", "limit"]),
        Self.tool("create_memory_candidate", "Create a pending startup-rule candidate",
             required: ["repo_root", "kind", "summary", "value"],
             optional: ["why_it_matters", "confidence", "proposed_by"]),
        Self.tool("propose_memory_merge", "Create or update a candidate merge proposal",
             required: ["repo_root", "candidate_id", "target_memory_id",
                        "proposed_title", "proposed_value"],
             optional: ["proposed_usage_hint", "risk_note", "proposed_by"]),
        Self.tool("list_memory_candidates", "List repository memory candidates",
             required: ["repo_root"], optional: ["status"]),
        Self.tool("create_checkpoint", "Create a durable repository checkpoint",
             required: ["repo_root", "conversation_id", "source_agent", "summary"],
             optional: ["resume_command", "metadata_json"]),
        Self.tool("build_handoff_packet", "Build and save an agent handoff packet",
             required: ["repo_root", "from_agent", "to_agent"],
             optional: ["goal_hint", "target_profile"]),
        Self.tool("list_active_runs", "List active repository runs that still need attention",
             required: ["repo_root"]),
        Self.tool("list_run_artifacts", "List artifacts produced by repository runs",
             required: ["repo_root"]),
        Self.tool("resume_from_checkpoint", "Resume repository work by promoting a checkpoint into a handoff packet",
             required: ["checkpoint_id", "to_agent"], optional: ["target_profile"]),
        Self.tool("list_repo_wiki_pages", "List generated repository wiki pages",
             required: ["repo_root"]),
        Self.tool("rebuild_repo_wiki", "Rebuild wiki projections from approved memory",
             required: ["repo_root"]),
        Self.tool("rebuild_repo_embeddings", "Rebuild the local repository search index",
             required: ["repo_root"]),
        Self.tool("list_memory_conflicts", "List open or filtered memory candidate conflicts that need review",
             required: ["repo_root"], optional: ["status"]),
        Self.tool("list_entity_graph", "List repository entity graph nodes and links",
             required: ["repo_root"], optional: ["limit"]),
        Self.tool("detect_agent_integrations", "Detect installed AI agents and CLIs without enabling missing products"),
    ] }

    func handle(line: String) async -> Data? {
        guard let data = line.data(using: .utf8),
              let request = try? JSONSerialization.jsonObject(with: data) as? [String: Any]
        else { return errorResponse(id: NSNull(), code: -32700, message: "Parse error") }
        let id = request["id"]
        let method = request["method"] as? String ?? ""
        if id == nil {
            return nil
        }
        do {
            let result: Any
            switch method {
            case "initialize":
                result = [
                    "protocolVersion": "2025-03-26",
                    "capabilities": ["tools": [:]],
                    "serverInfo": ["name": "aimemory", "version": "0.1.0"],
                ]
            case "ping":
                result = [:]
            case "tools/list":
                result = ["tools": toolDefinitions]
            case "tools/call":
                guard let params = request["params"] as? [String: Any],
                      let name = params["name"] as? String else {
                    throw MCPError.invalid("tools/call requires a tool name")
                }
                let arguments = params["arguments"] as? [String: Any] ?? [:]
                let payload = try await call(name: name, arguments: arguments)
                let payloadData = try JSONSerialization.data(
                    withJSONObject: payload,
                    options: [.sortedKeys]
                )
                result = [
                    "content": [[
                        "type": "text",
                        "text": String(data: payloadData, encoding: .utf8) ?? "{}",
                    ]],
                    "isError": false,
                ]
            default:
                return errorResponse(id: id ?? NSNull(), code: -32601, message: "Method not found")
            }
            return response(id: id ?? NSNull(), result: result)
        } catch {
            if method == "tools/call" {
                let payload: [String: Any] = [
                    "content": [[
                        "type": "text",
                        "text": error.localizedDescription,
                    ]],
                    "isError": true,
                ]
                return response(id: id ?? NSNull(), result: payload)
            }
            return errorResponse(
                id: id ?? NSNull(),
                code: -32602,
                message: error.localizedDescription
            )
        }
    }

    private func call(name: String, arguments: [String: Any]) async throws -> [String: Any] {
        switch name {
        case "get_repo_memory":
            let repo = try requiredString("repo_root", in: arguments)
            return [
                "repo_root": repo,
                "task_hint": arguments["task_hint"] as? String ?? "",
                "memories": try object(try await store.listApprovedMemories(repoRoot: repo)),
            ]
        case "get_project_context":
            let repo = try requiredString("repo_root", in: arguments)
            let query = try requiredString("query", in: arguments)
            let limit = boundedLimit(arguments["limit"], fallback: 3)
            let health = try await store.repoHealth(repoRoot: repo)
            let memories = try await store.listApprovedMemories(repoRoot: repo)
            let handoffs = try await store.listHandoffs(repoRoot: repo)
            let matches = try await store.searchRepoHistory(
                repoRoot: repo,
                text: query,
                limit: limit
            )
            return [
                "repo_root": repo,
                "query": query,
                "intent": arguments["intent"] as? String ?? "",
                "approved_memory": try object(Array(memories.prefix(limit))),
                "recent_handoff": try object(handoffs.first),
                "health": try object(health),
                "relevant_history": try object(matches),
            ]
        case "get_repo_memory_health", "scan_repo_conversations":
            let repo = try requiredString("repo_root", in: arguments)
            if name == "scan_repo_conversations" {
                _ = await history.scan(repoRoot: repo)
            }
            return try dictionary(try object(try await store.repoHealth(repoRoot: repo)))
        case "import_all_local_history":
            let report = await history.importAll()
            return [
                "imported": report.imported,
                "imported_count": report.total,
                "warnings": report.warnings,
            ]
        case "merge_repo_alias":
            let result = try await store.mergeRepoAlias(
                repoRoot: requiredString("repo_root", in: arguments),
                aliasRoot: requiredString("alias_root", in: arguments)
            )
            return [
                "repo_id": result.repoID,
                "repo_root": result.repoRoot,
                "alias_root": result.aliasRoot,
            ]
        case "search_repo_history":
            let matches = try await store.searchRepoHistory(
                repoRoot: requiredString("repo_root", in: arguments),
                text: requiredString("query", in: arguments),
                limit: boundedLimit(arguments["limit"], fallback: 5)
            )
            return ["matches": try object(matches)]
        case "read_history_conversation":
            let detail = try await store.readConversationByID(
                requiredString("conversation_id", in: arguments)
            )
            let limit = boundedLimit(arguments["limit"], fallback: 12)
            var payload = try dictionary(try object(detail))
            let messages = focusedMessages(
                detail.messages,
                messageID: arguments["message_id"] as? String,
                query: arguments["query"] as? String,
                limit: limit
            )
            payload["messages"] = try object(messages)
            payload["returned_message_count"] = messages.count
            let focusedID = messages.first(
                where: { $0.id == arguments["message_id"] as? String }
            )?.id
            payload["focused_message_id"] = focusedID ?? ""
            return payload
        case "create_memory_candidate":
            let id = try await store.createMemoryCandidate(
                repoRoot: requiredString("repo_root", in: arguments),
                kind: requiredString("kind", in: arguments),
                summary: requiredString("summary", in: arguments),
                value: requiredString("value", in: arguments),
                whyItMatters: arguments["why_it_matters"] as? String ?? "",
                confidence: (arguments["confidence"] as? NSNumber)?.doubleValue ?? 0.75,
                proposedBy: arguments["proposed_by"] as? String ?? "mcp"
            )
            return ["candidate_id": id, "status": "pending_review"]
        case "propose_memory_merge":
            let id = try await store.createMemoryMergeProposal(
                repoRoot: requiredString("repo_root", in: arguments),
                candidateID: requiredString("candidate_id", in: arguments),
                targetMemoryID: requiredString("target_memory_id", in: arguments),
                title: requiredString("proposed_title", in: arguments),
                value: requiredString("proposed_value", in: arguments),
                usageHint: arguments["proposed_usage_hint"] as? String ?? "",
                riskNote: arguments["risk_note"] as? String ?? "",
                proposedBy: arguments["proposed_by"] as? String ?? "mcp"
            )
            return ["proposal_id": id, "status": "pending_review"]
        case "create_checkpoint":
            let checkpoint = try await store.createCheckpoint(
                repoRoot: requiredString("repo_root", in: arguments),
                conversationID: requiredString("conversation_id", in: arguments),
                sourceAgent: requiredString("source_agent", in: arguments),
                summary: requiredString("summary", in: arguments),
                resumeCommand: arguments["resume_command"] as? String,
                metadataJSON: arguments["metadata_json"] as? String
            )
            return try dictionary(try object(checkpoint))
        case "list_memory_candidates":
            let candidates = try await store.listMemoryCandidates(
                repoRoot: requiredString("repo_root", in: arguments)
            )
            let status = arguments["status"] as? String
            let filtered = status?.isEmpty == false
                ? candidates.filter { $0.status == status }
                : candidates
            return ["candidates": try object(filtered)]
        case "build_handoff_packet":
            let handoff = try await store.createHandoff(
                repoRoot: requiredString("repo_root", in: arguments),
                fromAgent: requiredString("from_agent", in: arguments),
                toAgent: requiredString("to_agent", in: arguments),
                goalHint: arguments["goal_hint"] as? String,
                targetProfile: arguments["target_profile"] as? String
            )
            return try dictionary(try object(handoff))
        case "list_active_runs":
            let runs = try await store.listActiveRuns(
                repoRoot: requiredString("repo_root", in: arguments)
            )
            return ["runs": try object(runs)]
        case "list_run_artifacts":
            let artifacts = try await store.listRunArtifacts(
                repoRoot: requiredString("repo_root", in: arguments)
            )
            return ["artifacts": try object(artifacts)]
        case "resume_from_checkpoint":
            let handoff = try await store.resumeFromCheckpoint(
                checkpointID: requiredString("checkpoint_id", in: arguments),
                toAgent: requiredString("to_agent", in: arguments),
                targetProfile: arguments["target_profile"] as? String
            )
            return try dictionary(try object(handoff))
        case "list_repo_wiki_pages":
            let pages = try await store.listWikiPages(
                repoRoot: requiredString("repo_root", in: arguments)
            )
            return ["pages": try object(pages)]
        case "rebuild_repo_wiki":
            let pages = try await store.rebuildWiki(
                repoRoot: requiredString("repo_root", in: arguments)
            )
            return ["pages": try object(pages)]
        case "rebuild_repo_embeddings":
            let result = try await store.rebuildSearchIndex(
                repoRoot: requiredString("repo_root", in: arguments)
            )
            return [
                "document_count": result.documentCount,
                "embedding_count": result.embeddingCount,
                "model": "native-token-hash-v1",
            ]
        case "list_memory_conflicts":
            let conflicts = try await store.listMemoryConflicts(
                repoRoot: requiredString("repo_root", in: arguments),
                status: arguments["status"] as? String
            )
            return ["conflicts": try object(conflicts)]
        case "list_entity_graph":
            return try dictionary(try object(
                try await store.listEntityGraph(
                    repoRoot: requiredString("repo_root", in: arguments),
                    limit: boundedLimit(arguments["limit"], fallback: 25)
                )
            ))
        case "detect_agent_integrations":
            return ["integrations": try object(await integrations.detect())]
        default:
            throw MCPError.invalid("Unknown tool: \(name)")
        }
    }

    private func requiredString(_ key: String, in values: [String: Any]) throws -> String {
        guard let value = values[key] as? String,
              !value.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw MCPError.invalid("Missing required argument: \(key)")
        }
        return value
    }

    private func boundedLimit(_ value: Any?, fallback: Int) -> Int {
        max(1, min((value as? NSNumber)?.intValue ?? fallback, 50))
    }

    private func focusedMessages(
        _ messages: [ConversationMessage],
        messageID: String?,
        query: String?,
        limit: Int
    ) -> [ConversationMessage] {
        guard messages.count > limit else { return messages }
        let focusIndex: Int?
        if let messageID, !messageID.isEmpty {
            focusIndex = messages.firstIndex { $0.id == messageID }
        } else if let query, !query.isEmpty {
            focusIndex = messages.firstIndex {
                $0.content.localizedCaseInsensitiveContains(query)
            }
        } else {
            focusIndex = nil
        }
        guard let focusIndex else {
            return Array(messages.suffix(limit))
        }
        let lower = max(0, min(
            focusIndex - limit / 2,
            messages.count - limit
        ))
        return Array(messages[lower..<(lower + limit)])
    }

    private func object<T: Encodable>(_ value: T) throws -> Any {
        try JSONSerialization.jsonObject(with: encoder.encode(value))
    }

    private func object<T: Encodable>(_ value: T?) throws -> Any {
        guard let value else { return NSNull() }
        return try object(value)
    }

    private func dictionary(_ value: Any) throws -> [String: Any] {
        guard let dictionary = value as? [String: Any] else {
            throw MCPError.invalid("Internal result is not an object")
        }
        return dictionary
    }

    private func response(id: Any, result: Any) -> Data? {
        try? JSONSerialization.data(
            withJSONObject: ["jsonrpc": "2.0", "id": id, "result": result],
            options: [.sortedKeys]
        )
    }

    private func errorResponse(id: Any, code: Int, message: String) -> Data? {
        try? JSONSerialization.data(
            withJSONObject: [
                "jsonrpc": "2.0",
                "id": id,
                "error": ["code": code, "message": message],
            ],
            options: [.sortedKeys]
        )
    }

    private static func tool(
        _ name: String,
        _ description: String,
        required: [String] = [],
        optional: [String] = []
    ) -> [String: Any] {
        var properties: [String: Any] = [:]
        for key in required + optional {
            properties[key] = [
                "type": key == "limit" || key == "confidence"
                    ? "number" : "string"
            ]
        }
        return [
            "name": name,
            "description": description,
            "inputSchema": [
                "type": "object",
                "properties": properties,
                "required": required,
                "additionalProperties": false,
            ],
        ]
    }
}

private enum MCPError: LocalizedError {
    case invalid(String)

    var errorDescription: String? {
        if case .invalid(let message) = self { return message }
        return "Invalid MCP request"
    }
}
