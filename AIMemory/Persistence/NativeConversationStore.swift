// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
import Foundation
import SQLite3
import CryptoKit

/// Read-only conversation repository backed by AI Memory's own SQLite file.
///
/// This is the native replacement for the bridge's conversation read path.
/// It never opens ChatMem's database or an agent's history for writing.
actor NativeConversationStore {
    /// Upper bound for a single `tool_calls.input_json` value.
    ///
    /// Real tool inputs are a few kilobytes. Anything past 1 MB indicates
    /// recursive escaping or an oversized payload, both of which must never be
    /// persisted. See docs/TOOL_CALL_JSON_BLOAT.md.
    static let maxToolInputBytes = 1_048_576

    private let databaseURL: URL
    private let home: URL
    private let decoder = JSONDecoder()

    init(
        databaseURL: URL = DataPaths.dbURL,
        home: URL = FileManager.default.homeDirectoryForCurrentUser
    ) {
        self.databaseURL = databaseURL
        self.home = home
    }

    func detectSources() throws -> [ConversationSourceStatus] {
        let counts = try sourceCounts()
        let paths: [AgentKind: [URL]] = [
            .claude: [home.appendingPathComponent(".claude/projects")],
            .codex: [
                home.appendingPathComponent(".codex/state_5.sqlite"),
                home.appendingPathComponent(".codex/sessions"),
            ],
            .gemini: [home.appendingPathComponent(".gemini")],
            .antigravity: [
                home.appendingPathComponent(".gemini/antigravity"),
                home.appendingPathComponent(".antigravity"),
            ],
            .opencode: [
                home.appendingPathComponent(".local/share/opencode"),
                home.appendingPathComponent(".config/opencode"),
            ],
            .zcode: [home.appendingPathComponent(".zcode")],
            .hermes: [home.appendingPathComponent(".hermes")],
            .kimi: [
                home.appendingPathComponent(".kimi"),
                home.appendingPathComponent(".kimi-code"),
            ],
        ]

        return AgentKind.allCases.map { agent in
            ConversationSourceStatus(
                agent: agent.rawValue,
                label: agent.label,
                available: (counts[agent.rawValue] ?? 0) > 0
                    || (paths[agent]?.contains {
                        FileManager.default.fileExists(atPath: $0.path)
                    } ?? false)
            )
        }
    }

    func listConversations(agent: String) throws -> [ConversationSummary] {
        let rows = try query(
            """
            SELECT
                c.conversation_id AS id,
                c.source_agent AS source_agent,
                COALESCE(r.repo_root, '') AS project_dir,
                c.started_at AS created_at,
                c.updated_at AS updated_at,
                c.summary AS summary,
                (SELECT COUNT(*) FROM messages m
                    WHERE m.conversation_id = c.conversation_id) AS message_count,
                (SELECT COUNT(DISTINCT f.path) FROM file_changes f
                    WHERE f.conversation_id = c.conversation_id) AS file_count
            FROM conversations c
            LEFT JOIN repos r ON r.repo_id = c.repo_id
            WHERE lower(c.source_agent) = lower(?)
            ORDER BY c.updated_at DESC, c.conversation_id DESC;
            """,
            bindings: [.text(agent)]
        )
        return try decodeRows(rows, as: [ConversationSummary].self)
    }

    func searchConversations(agent: String, text: String) throws -> [ConversationSummary] {
        let pattern = "%\(text)%"
        let rows = try query(
            """
            SELECT
                c.conversation_id AS id,
                c.source_agent AS source_agent,
                COALESCE(r.repo_root, '') AS project_dir,
                c.started_at AS created_at,
                c.updated_at AS updated_at,
                c.summary AS summary,
                (SELECT COUNT(*) FROM messages m
                    WHERE m.conversation_id = c.conversation_id) AS message_count,
                (SELECT COUNT(DISTINCT f.path) FROM file_changes f
                    WHERE f.conversation_id = c.conversation_id) AS file_count
            FROM conversations c
            LEFT JOIN repos r ON r.repo_id = c.repo_id
            WHERE lower(c.source_agent) = lower(?)
              AND (
                COALESCE(c.summary, '') LIKE ? COLLATE NOCASE
                OR COALESCE(r.repo_root, '') LIKE ? COLLATE NOCASE
                OR EXISTS (
                    SELECT 1 FROM messages m
                    WHERE m.conversation_id = c.conversation_id
                      AND m.content LIKE ? COLLATE NOCASE
                )
              )
            ORDER BY c.updated_at DESC, c.conversation_id DESC;
            """,
            bindings: [.text(agent), .text(pattern), .text(pattern), .text(pattern)]
        )
        return try decodeRows(rows, as: [ConversationSummary].self)
    }

    /// Repository-scoped history search used by the native MCP service.
    /// Results are real indexed conversations, never synthesized snippets.
    func searchRepoHistory(
        repoRoot: String,
        text: String,
        limit: Int
    ) throws -> [ConversationSummary] {
        guard let repoID = try findRepoID(repoRoot: repoRoot) else { return [] }
        let bounded = max(1, min(limit, 50))
        let escaped = text
            .replacingOccurrences(of: "\\", with: "\\\\")
            .replacingOccurrences(of: "%", with: "\\%")
            .replacingOccurrences(of: "_", with: "\\_")
        let pattern = "%\(escaped)%"
        let rows = try query(
            """
            SELECT
              c.source_conversation_id AS id,
              c.source_agent AS source_agent,
              COALESCE(r.repo_root, '') AS project_dir,
              c.started_at AS created_at,
              c.updated_at AS updated_at,
              c.summary,
              (SELECT COUNT(*) FROM messages m
                 WHERE m.conversation_id = c.conversation_id) AS message_count,
              (SELECT COUNT(*) FROM file_changes f
                 WHERE f.conversation_id = c.conversation_id) AS file_count
            FROM conversations c
            LEFT JOIN repos r ON r.repo_id = c.repo_id
            WHERE c.repo_id = ?
              AND (
                COALESCE(c.summary, '') LIKE ? ESCAPE '\\'
                OR EXISTS(
                  SELECT 1 FROM messages m
                  WHERE m.conversation_id = c.conversation_id
                    AND m.content LIKE ? ESCAPE '\\'
                )
              )
            ORDER BY c.updated_at DESC
            LIMIT ?;
            """,
            bindings: [.text(repoID), .text(pattern), .text(pattern), .integer(bounded)]
        )
        return try decodeRows(rows, as: [ConversationSummary].self)
    }

    func readConversationByID(_ id: String) throws -> ConversationDetail {
        guard let row = try query(
            """
            SELECT source_agent, source_conversation_id
            FROM conversations
            WHERE conversation_id = ? OR source_conversation_id = ?
            ORDER BY updated_at DESC LIMIT 1;
            """,
            bindings: [.text(id), .text(id)]
        ).first,
        let agent = row["source_agent"] as? String,
        let sourceID = row["source_conversation_id"] as? String else {
            throw NativeConversationStoreError.notFound(agent: "history", id: id)
        }
        return try readConversation(agent: agent, id: sourceID)
    }

    func createMemoryCandidate(
        repoRoot: String,
        kind: String,
        summary: String,
        value: String,
        whyItMatters: String,
        confidence: Double,
        proposedBy: String
    ) throws -> String {
        let repoID = try ensureRepoID(repoRoot: repoRoot)
        let candidateID = UUID().uuidString
        let now = ISO8601DateFormatter().string(from: Date())
        try executeTransaction([
            (
                """
                INSERT INTO memory_candidates(
                  candidate_id, repo_id, kind, summary, value,
                  why_it_matters, confidence, proposed_by, status,
                  created_at, reviewed_at
                ) VALUES(?, ?, ?, ?, ?, ?, ?, ?, 'pending_review', ?, NULL);
                """,
                [
                    .text(candidateID), .text(repoID), .text(kind),
                    .text(summary), .text(value), .text(whyItMatters),
                    .double(max(0, min(confidence, 1))),
                    .text(proposedBy), .text(now),
                ]
            ),
        ])
        return candidateID
    }

    func createMemoryMergeProposal(
        repoRoot: String,
        candidateID: String,
        targetMemoryID: String,
        title: String,
        value: String,
        usageHint: String,
        riskNote: String,
        proposedBy: String
    ) throws -> String {
        let repoID = try ensureRepoID(repoRoot: repoRoot)
        let proposalID = UUID().uuidString
        let now = ISO8601DateFormatter().string(from: Date())
        try executeTransaction([
            (
                """
                INSERT INTO memory_merge_proposals(
                  proposal_id, repo_id, candidate_id, target_memory_id,
                  proposed_title, proposed_value, proposed_usage_hint,
                  risk_note, proposed_by, status, created_at, updated_at
                ) VALUES(?, ?, ?, ?, ?, ?, ?, ?, ?, 'pending_review', ?, ?)
                ON CONFLICT(candidate_id, target_memory_id) DO UPDATE SET
                  proposed_title = excluded.proposed_title,
                  proposed_value = excluded.proposed_value,
                  proposed_usage_hint = excluded.proposed_usage_hint,
                  risk_note = excluded.risk_note,
                  proposed_by = excluded.proposed_by,
                  status = 'pending_review',
                  updated_at = excluded.updated_at;
                """,
                [
                    .text(proposalID), .text(repoID), .text(candidateID),
                    .text(targetMemoryID), .text(title), .text(value),
                    .text(usageHint), .text(riskNote), .text(proposedBy),
                    .text(now), .text(now),
                ]
            ),
        ])
        return proposalID
    }

    func readConversation(agent: String, id: String) throws -> ConversationDetail {
        let conversations = try query(
            """
            SELECT
                c.conversation_id AS id,
                c.source_agent AS source_agent,
                COALESCE(r.repo_root, '') AS project_dir,
                c.started_at AS created_at,
                c.updated_at AS updated_at,
                c.summary AS summary,
                c.storage_path AS storage_path
            FROM conversations c
            LEFT JOIN repos r ON r.repo_id = c.repo_id
            WHERE lower(c.source_agent) = lower(?)
              AND c.conversation_id = ?
            LIMIT 1;
            """,
            bindings: [.text(agent), .text(id)]
        )
        guard var root = conversations.first else {
            throw NativeConversationStoreError.notFound(agent: agent, id: id)
        }

        let messageRows = try query(
            """
            SELECT message_id AS id, timestamp, role, content
            FROM messages
            WHERE conversation_id = ?
            ORDER BY timestamp ASC, rowid ASC;
            """,
            bindings: [.text(id)]
        )
        var messages: [[String: Any]] = []
        messages.reserveCapacity(messageRows.count)
        for var message in messageRows {
            let messageID = message["id"] as? String ?? ""
            let tools = try query(
                """
                SELECT tool_call_id AS id, name, input_json, output_text AS output, status
                FROM tool_calls
                WHERE message_id = ?
                ORDER BY rowid ASC;
                """,
                bindings: [.text(messageID)]
            ).map { row -> [String: Any] in
                var tool = row
                let inputText = tool.removeValue(forKey: "input_json") as? String ?? "{}"
                tool["input"] = Self.jsonObject(from: inputText)
                return tool
            }
            message["tool_calls"] = tools
            message["metadata"] = [String: Any]()
            messages.append(message)
        }

        let fileChanges = try query(
            """
            SELECT path, change_type, timestamp, message_id
            FROM file_changes
            WHERE conversation_id = ?
            ORDER BY timestamp ASC, rowid ASC;
            """,
            bindings: [.text(id)]
        )

        root["resume_command"] = Self.resumeCommand(agent: agent, id: id)
        root["messages"] = messages
        root["file_changes"] = fileChanges
        let data = try JSONSerialization.data(withJSONObject: root)
        return try decoder.decode(ConversationDetail.self, from: data)
    }

    /// Exports one consistent database snapshot for sync.
    ///
    /// The old WebDAV path called `listConversations` and then
    /// `readConversation` for every item. `readConversation` in turn opened a
    /// new SQLite connection for the conversation, messages, every message's
    /// tools, and file changes. A large history therefore performed tens of
    /// thousands of opens while the background importer was writing WAL
    /// frames, eventually surfacing `SQLITE_CANTOPEN`. Keep one WAL-aware
    /// connection and fetch the four related row sets in bulk instead.
    func exportAllConversationsForSync() throws -> [ConversationDetail] {
        let connection = try openQueryConnection()
        defer { sqlite3_close(connection) }

        let roots = try query(
            connection: connection,
            sql: """
            SELECT c.conversation_id AS id,
                   c.source_agent AS source_agent,
                   COALESCE(r.repo_root, '') AS project_dir,
                   c.started_at AS created_at,
                   c.updated_at AS updated_at,
                   c.summary AS summary,
                   c.storage_path AS storage_path
            FROM conversations c
            LEFT JOIN repos r ON r.repo_id = c.repo_id
            ORDER BY c.updated_at DESC, c.conversation_id DESC;
            """
        )
        let messageRows = try query(
            connection: connection,
            sql: """
            SELECT conversation_id,message_id AS id,timestamp,role,content
            FROM messages
            ORDER BY conversation_id,timestamp,rowid;
            """
        )
        let toolRows = try query(
            connection: connection,
            sql: """
            SELECT m.conversation_id,t.message_id,t.tool_call_id AS id,t.name,
                   t.input_json,t.output_text AS output,t.status
            FROM tool_calls t
            JOIN messages m ON m.message_id=t.message_id
            ORDER BY m.conversation_id,t.message_id,t.rowid;
            """
        )
        let changeRows = try query(
            connection: connection,
            sql: """
            SELECT conversation_id,path,change_type,timestamp,message_id
            FROM file_changes
            ORDER BY conversation_id,timestamp,rowid;
            """
        )

        var toolsByMessage: [String: [[String: Any]]] = [:]
        for var row in toolRows {
            let messageID = row.removeValue(forKey: "message_id") as? String ?? ""
            row.removeValue(forKey: "conversation_id")
            let inputText = row.removeValue(forKey: "input_json") as? String ?? "{}"
            row["input"] = Self.jsonObject(from: inputText)
            toolsByMessage[messageID, default: []].append(row)
        }

        var messagesByConversation: [String: [[String: Any]]] = [:]
        for var row in messageRows {
            let conversationID =
                row.removeValue(forKey: "conversation_id") as? String ?? ""
            let messageID = row["id"] as? String ?? ""
            row["tool_calls"] = toolsByMessage[messageID] ?? []
            row["metadata"] = [String: Any]()
            messagesByConversation[conversationID, default: []].append(row)
        }

        var changesByConversation: [String: [[String: Any]]] = [:]
        for var row in changeRows {
            let conversationID =
                row.removeValue(forKey: "conversation_id") as? String ?? ""
            changesByConversation[conversationID, default: []].append(row)
        }

        var details: [ConversationDetail] = []
        details.reserveCapacity(roots.count)
        for rootValue in roots {
            var root = rootValue
            let id = root["id"] as? String ?? ""
            let agent = root["source_agent"] as? String ?? ""
            root["resume_command"] = Self.resumeCommand(agent: agent, id: id)
            root["messages"] = messagesByConversation[id] ?? []
            root["file_changes"] = changesByConversation[id] ?? []
            details.append(try decodeObject(root, as: ConversationDetail.self))
        }
        return details
    }

    func repoHealth(repoRoot: String) throws -> RepoHealth {
        guard let repoID = try findRepoID(repoRoot: repoRoot) else {
            return try decodeObject(
                [
                    "repo_root": repoRoot,
                    "approved_memory_count": 0,
                    "pending_candidate_count": 0,
                    "indexed_chunk_count": 0,
                    "search_document_count": 0,
                ],
                as: RepoHealth.self
            )
        }
        let counts = try query(
            """
            SELECT
              (SELECT COUNT(*) FROM approved_memories
                 WHERE repo_id = ? AND status = 'active') AS approved_memory_count,
              (SELECT COUNT(*) FROM memory_candidates
                 WHERE repo_id = ? AND status IN ('pending', 'pending_review')) AS pending_candidate_count,
              (SELECT COUNT(*) FROM conversation_chunks
                 WHERE repo_id = ?) AS indexed_chunk_count,
              (SELECT COUNT(*) FROM search_documents
                 WHERE repo_id = ?) AS search_document_count;
            """,
            bindings: [.text(repoID), .text(repoID), .text(repoID), .text(repoID)]
        ).first ?? [:]
        var object = counts
        object["repo_root"] = repoRoot

        if var scan = try query(
            """
            SELECT scanned_conversation_count, linked_conversation_count,
                   skipped_conversation_count, unmatched_project_roots_json
            FROM repo_scan_runs
            WHERE repo_id = ?
            ORDER BY scanned_at DESC
            LIMIT 1;
            """,
            bindings: [.text(repoID)]
        ).first {
            let unmatched = scan.removeValue(forKey: "unmatched_project_roots_json") as? String
                ?? "[]"
            scan["unmatched_project_roots"] = Self.jsonObject(from: unmatched)
            object["latest_scan"] = scan
        }
        return try decodeObject(object, as: RepoHealth.self)
    }

    func listMemoryCandidates(repoRoot: String) throws -> [MemoryCandidate] {
        guard let repoID = try findRepoID(repoRoot: repoRoot) else { return [] }
        var rows = try query(
            """
            SELECT candidate_id, kind, summary, value, why_it_matters,
                   confidence, proposed_by, status, created_at
            FROM memory_candidates
            WHERE repo_id = ?
            ORDER BY created_at DESC, candidate_id DESC;
            """,
            bindings: [.text(repoID)]
        )
        for index in rows.indices {
            let candidateID = rows[index]["candidate_id"] as? String ?? ""
            rows[index]["evidence_refs"] = try evidence(ownerType: "candidate", ownerID: candidateID)
            if let proposal = try query(
                """
                SELECT target_memory_id, proposed_title, proposed_value,
                       proposed_usage_hint, risk_note, status
                FROM memory_merge_proposals
                WHERE candidate_id = ?
                ORDER BY updated_at DESC LIMIT 1;
                """,
                bindings: [.text(candidateID)]
            ).first {
                rows[index]["merge_suggestion"] = proposal
            }
            if let conflict = try query(
                """
                SELECT memory_id, reason, status
                FROM memory_conflicts
                WHERE candidate_id = ?
                ORDER BY created_at DESC LIMIT 1;
                """,
                bindings: [.text(candidateID)]
            ).first {
                rows[index]["conflict_suggestion"] = conflict
            }
        }
        return try decodeRows(rows, as: [MemoryCandidate].self)
    }

    func listApprovedMemories(repoRoot: String) throws -> [ApprovedMemory] {
        guard let repoID = try findRepoID(repoRoot: repoRoot) else { return [] }
        let rows = try query(
            """
            SELECT memory_id, kind, title, value, usage_hint, status,
                   last_verified_at, freshness_status, freshness_score
            FROM approved_memories
            WHERE repo_id = ?
            ORDER BY CASE status WHEN 'active' THEN 0 ELSE 1 END,
                     updated_at DESC, memory_id DESC;
            """,
            bindings: [.text(repoID)]
        )
        return try decodeRows(rows, as: [ApprovedMemory].self)
    }

    func listWikiPages(repoRoot: String) throws -> [WikiPage] {
        guard let repoID = try findRepoID(repoRoot: repoRoot) else { return [] }
        let rows = try query(
            """
            SELECT page_id, slug, title, body, status, last_built_at
            FROM wiki_pages
            WHERE repo_id = ?
            ORDER BY title COLLATE NOCASE, page_id;
            """,
            bindings: [.text(repoID)]
        )
        return try decodeRows(rows, as: [WikiPage].self)
    }

    func listCheckpoints(repoRoot: String) throws -> [Checkpoint] {
        guard let repoID = try findRepoID(repoRoot: repoRoot) else { return [] }
        let rows = try query(
            """
            SELECT cp.checkpoint_id, r.repo_root, cp.conversation_id,
                   cp.source_agent, cp.status, cp.summary, cp.resume_command,
                   cp.metadata_json, cp.handoff_id, cp.created_at
            FROM checkpoints cp
            JOIN repos r ON r.repo_id = cp.repo_id
            WHERE cp.repo_id = ?
            ORDER BY cp.created_at DESC, cp.checkpoint_id DESC;
            """,
            bindings: [.text(repoID)]
        )
        return try decodeRows(rows, as: [Checkpoint].self)
    }

    func listHandoffs(repoRoot: String) throws -> [HandoffPacket] {
        guard let repoID = try findRepoID(repoRoot: repoRoot) else { return [] }
        var rows = try query(
            """
            SELECT hp.handoff_id, r.repo_root, hp.from_agent, hp.to_agent,
                   hp.status, hp.checkpoint_id, hp.target_profile,
                   hp.current_goal, hp.done_json, hp.next_json,
                   hp.key_files_json, hp.commands_json, hp.created_at
            FROM handoff_packets hp
            JOIN repos r ON r.repo_id = hp.repo_id
            WHERE hp.repo_id = ?
            ORDER BY hp.created_at DESC, hp.handoff_id DESC;
            """,
            bindings: [.text(repoID)]
        )
        for index in rows.indices {
            rows[index]["done_items"] = Self.jsonObject(
                from: rows[index].removeValue(forKey: "done_json") as? String ?? "[]"
            )
            rows[index]["next_items"] = Self.jsonObject(
                from: rows[index].removeValue(forKey: "next_json") as? String ?? "[]"
            )
            rows[index]["key_files"] = Self.jsonObject(
                from: rows[index].removeValue(forKey: "key_files_json") as? String ?? "[]"
            )
            rows[index]["useful_commands"] = Self.jsonObject(
                from: rows[index].removeValue(forKey: "commands_json") as? String ?? "[]"
            )
        }
        return try decodeRows(rows, as: [HandoffPacket].self)
    }

    func listActiveRuns(repoRoot: String) throws -> [AgentRunRecord] {
        guard let repoID = try findRepoID(repoRoot: repoRoot) else { return [] }
        try seedRuns(repoID: repoID)
        let rows = try query(
            """
            SELECT ar.run_id, r.repo_root, ar.source_agent, ar.task_hint,
                   ar.status, ar.summary, ar.started_at, ar.ended_at,
                   COUNT(a.artifact_id) AS artifact_count
            FROM agent_runs ar
            JOIN repos r ON r.repo_id = ar.repo_id
            LEFT JOIN artifacts a ON a.run_id = ar.run_id
            WHERE ar.repo_id = ? AND ar.status <> 'completed'
            GROUP BY ar.run_id, r.repo_root, ar.source_agent, ar.task_hint,
                     ar.status, ar.summary, ar.started_at, ar.ended_at
            ORDER BY ar.started_at DESC;
            """,
            bindings: [.text(repoID)]
        )
        return try decodeRows(rows, as: [AgentRunRecord].self)
    }

    func listRunArtifacts(repoRoot: String) throws -> [RunArtifactRecord] {
        guard let repoID = try findRepoID(repoRoot: repoRoot) else { return [] }
        try seedRuns(repoID: repoID)
        let rows = try query(
            """
            SELECT a.artifact_id, a.run_id, a.artifact_type, a.title,
                   a.summary, a.trust_state, a.created_at
            FROM artifacts a
            JOIN agent_runs ar ON ar.run_id = a.run_id
            WHERE ar.repo_id = ?
            ORDER BY a.created_at DESC, a.artifact_id DESC;
            """,
            bindings: [.text(repoID)]
        )
        return try decodeRows(rows, as: [RunArtifactRecord].self)
    }

    func listMemoryConflicts(
        repoRoot: String,
        status: String?
    ) throws -> [MemoryConflictRecord] {
        guard let repoID = try findRepoID(repoRoot: repoRoot) else { return [] }
        var sql = """
            SELECT mc.conflict_id, mc.candidate_id, mc.memory_id,
                   am.title AS memory_title, mc.reason, mc.status, mc.created_at
            FROM memory_conflicts mc
            JOIN approved_memories am ON am.memory_id = mc.memory_id
            WHERE mc.repo_id = ?
            """
        var bindings: [Binding] = [.text(repoID)]
        if let status, !status.isEmpty {
            sql += " AND mc.status = ?"
            bindings.append(.text(status))
        }
        sql += " ORDER BY mc.created_at DESC;"
        return try decodeRows(
            try query(sql, bindings: bindings),
            as: [MemoryConflictRecord].self
        )
    }

    func listEpisodes(repoRoot: String) throws -> [EpisodeRecord] {
        guard let repoID = try findRepoID(repoRoot: repoRoot) else { return [] }
        let rows = try query(
            """
            SELECT episode_id, title, summary, outcome, created_at,
                   source_conversation_id
            FROM episodes
            WHERE repo_id = ?
            ORDER BY created_at DESC, episode_id DESC;
            """,
            bindings: [.text(repoID)]
        )
        return try decodeRows(rows, as: [EpisodeRecord].self)
    }

    func listEntityGraph(repoRoot: String, limit: Int) throws -> MemoryEntityGraph {
        guard let repoID = try findRepoID(repoRoot: repoRoot) else {
            return MemoryEntityGraph(entities: [], links: [])
        }
        let bounded = max(1, min(limit, 100))
        let entities = try decodeRows(
            try query(
                """
                SELECT e.entity_id, e.name, e.kind,
                       COUNT(l.link_id) AS mention_count
                FROM memory_entities e
                LEFT JOIN memory_entity_links l ON l.entity_id = e.entity_id
                WHERE e.repo_id = ?
                GROUP BY e.entity_id, e.name, e.kind
                ORDER BY mention_count DESC, e.updated_at DESC
                LIMIT ?;
                """,
                bindings: [.text(repoID), .integer(bounded)]
            ),
            as: [MemoryEntityNode].self
        )
        guard !entities.isEmpty else {
            return MemoryEntityGraph(entities: [], links: [])
        }
        let selected = Set(entities.map(\.entityID))
        let allLinks = try decodeRows(
            try query(
                """
                SELECT l.entity_id, e.name AS entity_name, l.owner_type,
                       l.owner_id, l.relationship,
                       COALESCE(sd.title, cc.title, l.owner_id) AS source_title,
                       cc.conversation_id AS source_conversation_id
                FROM memory_entity_links l
                JOIN memory_entities e ON e.entity_id = l.entity_id
                LEFT JOIN conversation_chunks cc
                  ON l.owner_type = 'chunk'
                 AND cc.chunk_id = l.owner_id
                LEFT JOIN search_documents sd
                  ON sd.repo_id = l.repo_id
                 AND sd.doc_ref_id = l.owner_id
                 AND ((l.owner_type = 'memory' AND sd.doc_type = 'memory')
                   OR (l.owner_type = 'episode' AND sd.doc_type = 'episode')
                   OR (l.owner_type = 'wiki_page' AND sd.doc_type = 'wiki')
                   OR (l.owner_type = 'conversation' AND sd.doc_type = 'conversation'))
                WHERE l.repo_id = ?
                ORDER BY l.created_at DESC
                LIMIT ?;
                """,
                bindings: [.text(repoID), .integer(bounded * 4)]
            ),
            as: [MemoryEntityLink].self
        )
        return MemoryEntityGraph(
            entities: entities,
            links: allLinks.filter { selected.contains($0.entityID) }
        )
    }

    func resumeFromCheckpoint(
        checkpointID: String,
        toAgent: String,
        targetProfile: String?
    ) throws -> HandoffPacket {
        guard let checkpoint = try query(
            """
            SELECT cp.repo_id, r.repo_root, cp.conversation_id,
                   cp.source_agent, cp.status, cp.summary,
                   cp.resume_command, cp.handoff_id
            FROM checkpoints cp
            JOIN repos r ON r.repo_id = cp.repo_id
            WHERE cp.checkpoint_id = ?
            LIMIT 1;
            """,
            bindings: [.text(checkpointID)]
        ).first else {
            throw NativeConversationStoreError.notFound(
                agent: "checkpoint",
                id: checkpointID
            )
        }
        let status = checkpoint["status"] as? String ?? ""
        let existingHandoff = checkpoint["handoff_id"] as? String ?? ""
        guard status == "active", existingHandoff.isEmpty else {
            throw NativeConversationStoreError.database(
                "checkpoint \(checkpointID) 已被提升，不能重复恢复。"
            )
        }
        let repoID = checkpoint["repo_id"] as? String ?? ""
        let repoRoot = checkpoint["repo_root"] as? String ?? ""
        let conversationID = checkpoint["conversation_id"] as? String ?? ""
        let sourceAgent = checkpoint["source_agent"] as? String ?? ""
        let summary = checkpoint["summary"] as? String ?? ""
        let resumeCommand = checkpoint["resume_command"] as? String ?? ""
        let handoffID = UUID().uuidString
        let now = ISO8601DateFormatter().string(from: Date())
        let keyFiles = try query(
            """
            SELECT DISTINCT path
            FROM file_changes
            WHERE conversation_id = ?
            ORDER BY timestamp DESC
            LIMIT 5;
            """,
            bindings: [.text(conversationID)]
        ).compactMap { $0["path"] as? String }
        var commands = try query(
            """
            SELECT value
            FROM approved_memories
            WHERE repo_id = ? AND status = 'active' AND kind = 'command'
            ORDER BY updated_at DESC
            LIMIT 3;
            """,
            bindings: [.text(repoID)]
        ).compactMap { $0["value"] as? String }
        if !resumeCommand.isEmpty, !commands.contains(resumeCommand) {
            commands.insert(resumeCommand, at: 0)
        }
        let done = ["已从 \(sourceAgent) checkpoint 固化上下文：\(summary)"]
        var next = [summary]
        if !resumeCommand.isEmpty { next.append("Resume with: \(resumeCommand)") }
        try executeTransaction([
            (
                """
                INSERT INTO handoff_packets(
                  handoff_id, repo_id, from_agent, to_agent, current_goal,
                  done_json, next_json, key_files_json, commands_json,
                  related_memories_json, related_episodes_json, created_at,
                  status, target_profile, checkpoint_id,
                  compression_strategy, consumed_at, consumed_by
                ) VALUES(?, ?, ?, ?, ?, ?, ?, ?, ?, '[]', '[]', ?,
                         'draft', ?, ?, 'source-backed', NULL, NULL);
                """,
                [
                    .text(handoffID), .text(repoID), .text(sourceAgent),
                    .text(toAgent), .text(summary), .text(Self.jsonString(done)),
                    .text(Self.jsonString(next)), .text(Self.jsonString(keyFiles)),
                    .text(Self.jsonString(commands)), .text(now),
                    .text(targetProfile ?? ""), .text(checkpointID),
                ]
            ),
            (
                """
                UPDATE checkpoints
                SET status = 'promoted', handoff_id = ?
                WHERE checkpoint_id = ? AND status = 'active'
                  AND handoff_id IS NULL;
                """,
                [.text(handoffID), .text(checkpointID)]
            ),
        ])
        guard let handoff = try listHandoffs(repoRoot: repoRoot).first(
            where: { $0.handoffID == handoffID }
        ) else {
            throw NativeConversationStoreError.database(
                "checkpoint 恢复后无法读取 handoff。"
            )
        }
        return handoff
    }

    private func seedRuns(repoID: String) throws {
        let conversations = try query(
            """
            SELECT conversation_id, source_agent, COALESCE(summary, '') AS summary,
                   source_conversation_id, started_at, updated_at
            FROM conversations
            WHERE repo_id = ?
            ORDER BY started_at DESC;
            """,
            bindings: [.text(repoID)]
        )
        var statements: [(String, [Binding])] = []
        for conversation in conversations {
            let conversationID = conversation["conversation_id"] as? String ?? ""
            guard !conversationID.isEmpty else { continue }
            let runID = "run:\(conversationID)"
            let paths = try query(
                """
                SELECT DISTINCT path FROM file_changes
                WHERE conversation_id = ?
                ORDER BY timestamp, path;
                """,
                bindings: [.text(conversationID)]
            ).compactMap { $0["path"] as? String }
            let tools = try query(
                """
                SELECT DISTINCT tc.name
                FROM tool_calls tc
                JOIN messages m ON m.message_id = tc.message_id
                WHERE m.conversation_id = ?
                ORDER BY tc.name COLLATE NOCASE;
                """,
                bindings: [.text(conversationID)]
            ).compactMap { $0["name"] as? String }
            let failures = try query(
                """
                SELECT COUNT(*) AS count
                FROM tool_calls tc
                JOIN messages m ON m.message_id = tc.message_id
                WHERE m.conversation_id = ?
                  AND lower(tc.status) IN ('failed', 'error');
                """,
                bindings: [.text(conversationID)]
            ).first?["count"] as? Int ?? 0
            let summary = conversation["summary"] as? String ?? ""
            let sourceID = conversation["source_conversation_id"] as? String ?? conversationID
            let effectiveSummary = summary.trimmingCharacters(
                in: .whitespacesAndNewlines
            ).isEmpty ? sourceID : summary
            let status = failures > 0
                ? "failed"
                : ((!paths.isEmpty || !tools.isEmpty) ? "waiting_for_review" : "completed")
            statements.append((
                """
                INSERT INTO agent_runs(
                  run_id, repo_id, source_agent, task_hint, status,
                  summary, started_at, ended_at
                ) VALUES(?, ?, ?, ?, ?, ?, ?, ?)
                ON CONFLICT(run_id) DO UPDATE SET
                  repo_id = excluded.repo_id,
                  source_agent = excluded.source_agent,
                  task_hint = excluded.task_hint,
                  status = excluded.status,
                  summary = excluded.summary,
                  started_at = excluded.started_at,
                  ended_at = excluded.ended_at;
                """,
                [
                    .text(runID), .text(repoID),
                    .text(conversation["source_agent"] as? String ?? ""),
                    .text(summary), .text(status), .text(effectiveSummary),
                    .text(conversation["started_at"] as? String ?? ""),
                    .text(conversation["updated_at"] as? String ?? ""),
                ]
            ))
            statements.append((
                "DELETE FROM artifacts WHERE run_id = ?;",
                [.text(runID)]
            ))
            if !paths.isEmpty {
                let preview = paths.prefix(12).joined(separator: "\n")
                    + (paths.count > 12 ? "\n…以及 \(paths.count - 12) 个文件" : "")
                statements.append((
                    """
                    INSERT INTO artifacts(
                      artifact_id, run_id, artifact_type, title, summary,
                      body, file_path, trust_state, created_at
                    ) VALUES(?, ?, 'file_change_set', 'Repository file changes',
                             ?, NULL, NULL, 'pending_review', ?);
                    """,
                    [
                        .text("\(runID):files"), .text(runID), .text(preview),
                        .text(conversation["updated_at"] as? String ?? ""),
                    ]
                ))
            }
            if !tools.isEmpty {
                statements.append((
                    """
                    INSERT INTO artifacts(
                      artifact_id, run_id, artifact_type, title, summary,
                      body, file_path, trust_state, created_at
                    ) VALUES(?, ?, 'tool_output_digest', 'Tool call outputs',
                             ?, NULL, NULL, 'pending_review', ?);
                    """,
                    [
                        .text("\(runID):tools"), .text(runID),
                        .text(tools.joined(separator: ", ")),
                        .text(conversation["updated_at"] as? String ?? ""),
                    ]
                ))
            }
        }
        if !statements.isEmpty {
            try executeTransaction(statements)
        }
    }

    func listReposWithCandidates() throws -> [RepoCandidateCount] {
        let rows = try query(
            """
            SELECT r.repo_root,
                   SUM(CASE WHEN c.status IN ('pending', 'pending_review') THEN 1 ELSE 0 END)
                     AS pending_count
            FROM memory_candidates c
            JOIN repos r ON r.repo_id = c.repo_id
            GROUP BY r.repo_id, r.repo_root
            HAVING pending_count > 0
            ORDER BY pending_count DESC, r.repo_root COLLATE NOCASE;
            """
        )
        return try decodeRows(rows, as: [RepoCandidateCount].self)
    }

    func reviewCandidate(
        id: String,
        action: String,
        title: String,
        value: String,
        usageHint: String,
        targetMemoryID: String
    ) throws {
        guard let candidate = try query(
            """
            SELECT repo_id, kind, summary, value
            FROM memory_candidates
            WHERE candidate_id = ?
            LIMIT 1;
            """,
            bindings: [.text(id)]
        ).first else {
            throw NativeConversationStoreError.notFound(agent: "memory-candidate", id: id)
        }
        let now = ISO8601DateFormatter().string(from: Date())
        switch action {
        case "approve", "approve_with_edit":
            let memoryID = UUID().uuidString
            let effectiveTitle = title.isEmpty
                ? (candidate["summary"] as? String ?? "")
                : title
            let effectiveValue = value.isEmpty
                ? (candidate["value"] as? String ?? "")
                : value
            try executeTransaction([
                (
                    """
                    INSERT INTO approved_memories(
                      memory_id, repo_id, kind, title, value, usage_hint,
                      status, last_verified_at, created_from_candidate_id,
                      created_at, updated_at, freshness_status, freshness_score,
                      verified_at, verified_by
                    ) VALUES(?, ?, ?, ?, ?, ?, 'active', ?, ?, ?, ?,
                             'fresh', 1.0, ?, 'user');
                    """,
                    [
                        .text(memoryID),
                        .text(candidate["repo_id"] as? String ?? ""),
                        .text(candidate["kind"] as? String ?? ""),
                        .text(effectiveTitle),
                        .text(effectiveValue),
                        .text(usageHint),
                        .text(now),
                        .text(id),
                        .text(now),
                        .text(now),
                        .text(now),
                    ]
                ),
                (
                    """
                    UPDATE memory_candidates
                    SET status = 'approved', reviewed_at = ?
                    WHERE candidate_id = ?;
                    """,
                    [.text(now), .text(id)]
                ),
            ])
        case "approve_merge":
            guard !targetMemoryID.isEmpty else {
                throw NativeConversationStoreError.database("合并候选缺少目标规则。")
            }
            let effectiveTitle = title.isEmpty
                ? (candidate["summary"] as? String ?? "")
                : title
            let effectiveValue = value.isEmpty
                ? (candidate["value"] as? String ?? "")
                : value
            try executeTransaction([
                (
                    """
                    UPDATE approved_memories
                    SET title = ?, value = ?, usage_hint = ?, status = 'active',
                        updated_at = ?, last_verified_at = ?,
                        freshness_status = 'fresh', freshness_score = 1.0
                    WHERE memory_id = ?;
                    """,
                    [
                        .text(effectiveTitle),
                        .text(effectiveValue),
                        .text(usageHint),
                        .text(now),
                        .text(now),
                        .text(targetMemoryID),
                    ]
                ),
                (
                    """
                    UPDATE memory_candidates
                    SET status = 'merged', reviewed_at = ?
                    WHERE candidate_id = ?;
                    """,
                    [.text(now), .text(id)]
                ),
                (
                    """
                    UPDATE memory_merge_proposals
                    SET status = 'approved', updated_at = ?
                    WHERE candidate_id = ? AND target_memory_id = ?;
                    """,
                    [.text(now), .text(id), .text(targetMemoryID)]
                ),
            ])
        case "reject":
            try executeTransaction([
                (
                    """
                    UPDATE memory_candidates
                    SET status = 'rejected', reviewed_at = ?
                    WHERE candidate_id = ?;
                    """,
                    [.text(now), .text(id)]
                ),
            ])
        case "snooze":
            try executeTransaction([
                (
                    """
                    UPDATE memory_candidates
                    SET status = 'snoozed', reviewed_at = ?
                    WHERE candidate_id = ?;
                    """,
                    [.text(now), .text(id)]
                ),
            ])
        default:
            throw NativeConversationStoreError.database("不支持的候选操作：\(action)")
        }
    }

    func retireMemory(id: String) throws {
        try executeTransaction([
            (
                """
                UPDATE approved_memories
                SET status = 'retired', updated_at = ?
                WHERE memory_id = ?;
                """,
                [.text(ISO8601DateFormatter().string(from: Date())), .text(id)]
            ),
        ])
    }

    func reverifyMemory(id: String) throws {
        let now = ISO8601DateFormatter().string(from: Date())
        try executeTransaction([
            (
                """
                UPDATE approved_memories
                SET status = 'active', last_verified_at = ?, verified_at = ?,
                    verified_by = 'user', freshness_status = 'fresh',
                    freshness_score = 1.0, updated_at = ?
                WHERE memory_id = ?;
                """,
                [.text(now), .text(now), .text(now), .text(id)]
            ),
        ])
    }

    func markHandoffConsumed(id: String) throws {
        try executeTransaction([
            (
                """
                UPDATE handoff_packets
                SET status = 'consumed', consumed_at = ?, consumed_by = 'user'
                WHERE handoff_id = ?;
                """,
                [.text(ISO8601DateFormatter().string(from: Date())), .text(id)]
            ),
        ])
    }

    func upsertConversation(_ conversation: ConversationDetail) throws {
        let repoRoot = conversation.projectDir.isEmpty
            ? "chatmem://unscoped/\(conversation.sourceAgent)"
            : conversation.projectDir
        let repoID = try ensureRepoID(repoRoot: repoRoot)
        var statements: [(String, [Binding])] = [
            (
                """
                INSERT INTO conversations(
                  conversation_id, repo_id, source_agent, source_conversation_id,
                  summary, started_at, updated_at, storage_path
                ) VALUES(?, ?, ?, ?, ?, ?, ?, ?)
                ON CONFLICT(conversation_id) DO UPDATE SET
                  repo_id = excluded.repo_id,
                  source_agent = excluded.source_agent,
                  source_conversation_id = excluded.source_conversation_id,
                  summary = excluded.summary,
                  started_at = excluded.started_at,
                  updated_at = excluded.updated_at,
                  storage_path = excluded.storage_path;
                """,
                [
                    .text(conversation.id),
                    .text(repoID),
                    .text(conversation.sourceAgent),
                    .text(conversation.id),
                    .text(conversation.summary ?? ""),
                    .text(conversation.createdAt),
                    .text(conversation.updatedAt),
                    .text(conversation.storagePath ?? ""),
                ]
            ),
            (
                """
                DELETE FROM tool_calls
                WHERE message_id IN (
                  SELECT message_id FROM messages WHERE conversation_id = ?
                );
                """,
                [.text(conversation.id)]
            ),
            (
                "DELETE FROM messages WHERE conversation_id = ?;",
                [.text(conversation.id)]
            ),
            (
                "DELETE FROM file_changes WHERE conversation_id = ?;",
                [.text(conversation.id)]
            ),
        ]
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys, .withoutEscapingSlashes]
        for message in conversation.messages {
            statements.append(
                (
                    """
                    INSERT INTO messages(message_id, conversation_id, role, content, timestamp)
                    VALUES(?, ?, ?, ?, ?);
                    """,
                    [
                        .text(message.id), .text(conversation.id), .text(message.role),
                        .text(message.content), .text(message.timestamp),
                    ]
                )
            )
            for tool in message.toolCalls {
                var input = (try? encoder.encode(tool.input))
                    .flatMap { String(data: $0, encoding: .utf8) } ?? "null"
                // Defence in depth: a tool input this large is never legitimate.
                // It means either runaway escaping or an oversized payload, and
                // letting it into the table is what previously grew the database
                // to 19 GB. Truncate with a diagnosable marker instead.
                if input.utf8.count > Self.maxToolInputBytes {
                    input = Self.jsonString([
                        "_truncated": true,
                        "_original_bytes": input.utf8.count,
                        "_preview": String(input.prefix(2_000)),
                    ])
                }
                statements.append(
                    (
                        """
                        INSERT INTO tool_calls(
                          tool_call_id, message_id, name, input_json, output_text, status
                        ) VALUES(?, ?, ?, ?, ?, ?);
                        """,
                        [
                            .text(tool.id), .text(message.id), .text(tool.name),
                            .text(input), .text(tool.output ?? ""), .text(tool.status),
                        ]
                    )
                )
            }
        }
        for change in conversation.fileChanges {
            statements.append(
                (
                    """
                    INSERT INTO file_changes(
                      file_change_id, conversation_id, message_id,
                      path, change_type, timestamp
                    ) VALUES(?, ?, ?, ?, ?, ?);
                    """,
                    [
                        .text(change.id), .text(conversation.id),
                        .text(change.messageId ?? ""), .text(change.path),
                        .text(change.changeType), .text(change.timestamp),
                    ]
                )
            )
        }
        try executeTransaction(statements)
    }

    func deleteIndexedConversation(agent: String, id: String) throws {
        let matches = try query(
            """
            SELECT COUNT(*) AS count
            FROM conversations
            WHERE lower(source_agent) = lower(?) AND conversation_id = ?;
            """,
            bindings: [.text(agent), .text(id)]
        ).first?["count"] as? Int ?? 0
        guard matches > 0 else {
            throw NativeConversationStoreError.notFound(agent: agent, id: id)
        }
        try executeTransaction([
            (
                """
                DELETE FROM tool_calls
                WHERE message_id IN (
                  SELECT message_id FROM messages WHERE conversation_id = ?
                );
                """,
                [.text(id)]
            ),
            ("DELETE FROM file_changes WHERE conversation_id = ?;", [.text(id)]),
            ("DELETE FROM messages WHERE conversation_id = ?;", [.text(id)]),
            ("DELETE FROM conversation_chunks WHERE conversation_id = ?;", [.text(id)]),
            (
                "DELETE FROM conversation_repo_links WHERE conversation_id = ?;",
                [.text(id)]
            ),
            (
                """
                DELETE FROM search_documents
                WHERE doc_type = 'conversation' AND doc_ref_id = ?;
                """,
                [.text(id)]
            ),
            (
                """
                DELETE FROM conversations
                WHERE lower(source_agent) = lower(?) AND conversation_id = ?;
                """,
                [.text(agent), .text(id)]
            ),
        ])
    }

    func createCheckpoint(
        repoRoot: String,
        conversationID: String,
        sourceAgent: String,
        summary: String,
        resumeCommand: String?,
        metadataJSON: String?
    ) throws -> Checkpoint {
        let repoID = try ensureRepoID(repoRoot: repoRoot)
        let checkpointID = UUID().uuidString
        let now = ISO8601DateFormatter().string(from: Date())
        let metadata = metadataJSON?.isEmpty == false ? metadataJSON! : "{}"
        guard let data = metadata.data(using: .utf8),
              (try? JSONSerialization.jsonObject(with: data)) != nil else {
            throw NativeConversationStoreError.database("checkpoint metadata 不是有效 JSON。")
        }
        try executeTransaction([
            (
                """
                INSERT INTO checkpoints(
                  checkpoint_id, repo_id, conversation_id, source_agent,
                  status, summary, resume_command, metadata_json,
                  handoff_id, created_at
                ) VALUES(?, ?, ?, ?, 'active', ?, ?, ?, NULL, ?);
                """,
                [
                    .text(checkpointID), .text(repoID), .text(conversationID),
                    .text(sourceAgent), .text(summary), .text(resumeCommand ?? ""),
                    .text(metadata), .text(now),
                ]
            ),
        ])
        guard let checkpoint = try listCheckpoints(repoRoot: repoRoot).first(
            where: { $0.checkpointID == checkpointID }
        ) else {
            throw NativeConversationStoreError.database("checkpoint 写入后无法读取。")
        }
        return checkpoint
    }

    /// Keeps one active automatic recovery point per conversation, matching
    /// ChatMem's `upsert_auto_checkpoint` behavior.
    func upsertAutoCheckpoint(
        repoRoot: String,
        conversationID: String,
        sourceAgent: String,
        summary: String,
        resumeCommand: String?,
        metadataJSON: String
    ) throws -> Checkpoint {
        let repoID = try ensureRepoID(repoRoot: repoRoot)
        guard let metadataData = metadataJSON.data(using: .utf8),
              let metadata = try? JSONSerialization.jsonObject(
                with: metadataData
              ) as? [String: Any],
              metadata["capture"] as? String == "auto" else {
            throw NativeConversationStoreError.database(
                "自动恢复点 metadata 必须包含 capture=auto。"
            )
        }

        let existing = try query(
            """
            SELECT checkpoint_id, metadata_json
            FROM checkpoints
            WHERE repo_id = ?
              AND conversation_id = ?
              AND lower(source_agent) = lower(?)
              AND status = 'active'
              AND handoff_id IS NULL
            ORDER BY created_at DESC;
            """,
            bindings: [.text(repoID), .text(conversationID), .text(sourceAgent)]
        ).first { row in
            guard let raw = row["metadata_json"] as? String,
                  let data = raw.data(using: .utf8),
                  let object = try? JSONSerialization.jsonObject(
                    with: data
                  ) as? [String: Any] else {
                return false
            }
            return object["capture"] as? String == "auto"
        }

        let checkpointID = existing?["checkpoint_id"] as? String
            ?? UUID().uuidString
        let now = ISO8601DateFormatter().string(from: Date())
        if existing == nil {
            try executeTransaction([
                (
                    """
                    INSERT INTO checkpoints(
                      checkpoint_id, repo_id, conversation_id, source_agent,
                      status, summary, resume_command, metadata_json,
                      handoff_id, created_at
                    ) VALUES(?, ?, ?, ?, 'active', ?, ?, ?, NULL, ?);
                    """,
                    [
                        .text(checkpointID), .text(repoID), .text(conversationID),
                        .text(sourceAgent), .text(summary),
                        .text(resumeCommand ?? ""), .text(metadataJSON), .text(now),
                    ]
                ),
            ])
        } else {
            try executeTransaction([
                (
                    """
                    UPDATE checkpoints
                    SET summary = ?, resume_command = ?, metadata_json = ?,
                        created_at = ?
                    WHERE checkpoint_id = ?;
                    """,
                    [
                        .text(summary), .text(resumeCommand ?? ""),
                        .text(metadataJSON), .text(now), .text(checkpointID),
                    ]
                ),
            ])
        }

        guard let checkpoint = try listCheckpoints(repoRoot: repoRoot).first(
            where: { $0.checkpointID == checkpointID }
        ) else {
            throw NativeConversationStoreError.database(
                "自动恢复点写入后无法读取。"
            )
        }
        return checkpoint
    }

    func createHandoff(
        repoRoot: String,
        fromAgent: String,
        toAgent: String,
        goalHint: String?,
        targetProfile: String?
    ) throws -> HandoffPacket {
        let repoID = try ensureRepoID(repoRoot: repoRoot)
        let handoffID = UUID().uuidString
        let now = ISO8601DateFormatter().string(from: Date())
        let goal: String
        if let goalHint, !goalHint.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            goal = goalHint
        } else {
            goal = try query(
                """
                SELECT COALESCE(summary, '') AS summary
                FROM conversations
                WHERE repo_id = ?
                ORDER BY updated_at DESC
                LIMIT 1;
                """,
                bindings: [.text(repoID)]
            ).first?["summary"] as? String ?? ""
        }
        let keyFiles = try query(
            """
            SELECT DISTINCT f.path
            FROM file_changes f
            JOIN conversations c ON c.conversation_id = f.conversation_id
            WHERE c.repo_id = ?
            ORDER BY f.timestamp DESC
            LIMIT 12;
            """,
            bindings: [.text(repoID)]
        ).compactMap { $0["path"] as? String }
        let commands = try query(
            """
            SELECT value
            FROM approved_memories
            WHERE repo_id = ? AND status = 'active' AND kind = 'command'
            ORDER BY updated_at DESC
            LIMIT 10;
            """,
            bindings: [.text(repoID)]
        ).compactMap { $0["value"] as? String }
        let encodeArray: ([String]) -> String = {
            let data = try? JSONSerialization.data(withJSONObject: $0)
            return data.flatMap { String(data: $0, encoding: .utf8) } ?? "[]"
        }
        try executeTransaction([
            (
                """
                INSERT INTO handoff_packets(
                  handoff_id, repo_id, from_agent, to_agent, current_goal,
                  done_json, next_json, key_files_json, commands_json,
                  related_memories_json, related_episodes_json, created_at,
                  status, target_profile, checkpoint_id,
                  compression_strategy, consumed_at, consumed_by
                ) VALUES(?, ?, ?, ?, ?, '[]', '[]', ?, ?, '[]', '[]', ?,
                         'draft', ?, NULL, 'source-backed', NULL, NULL);
                """,
                [
                    .text(handoffID), .text(repoID), .text(fromAgent), .text(toAgent),
                    .text(goal), .text(encodeArray(keyFiles)), .text(encodeArray(commands)),
                    .text(now), .text(targetProfile ?? ""),
                ]
            ),
        ])
        guard let handoff = try listHandoffs(repoRoot: repoRoot).first(
            where: { $0.handoffID == handoffID }
        ) else {
            throw NativeConversationStoreError.database("handoff 写入后无法读取。")
        }
        return handoff
    }

    func rebuildWiki(repoRoot: String) throws -> [WikiPage] {
        let repoID = try ensureRepoID(repoRoot: repoRoot)
        let memories = try query(
            """
            SELECT memory_id, kind, title, value, usage_hint
            FROM approved_memories
            WHERE repo_id = ? AND status = 'active'
            ORDER BY kind, title COLLATE NOCASE;
            """,
            bindings: [.text(repoID)]
        )
        let episodes = try query(
            """
            SELECT episode_id, title, summary, outcome
            FROM episodes
            WHERE repo_id = ?
            ORDER BY created_at DESC;
            """,
            bindings: [.text(repoID)]
        )
        var grouped: [String: [[String: Any]]] = [:]
        for memory in memories {
            grouped[memory["kind"] as? String ?? "memory", default: []].append(memory)
        }
        let now = ISO8601DateFormatter().string(from: Date())
        var statements: [(String, [Binding])] = [
            (
                "DELETE FROM wiki_pages WHERE repo_id = ? AND status = 'generated';",
                [.text(repoID)]
            ),
        ]
        for kind in grouped.keys.sorted() {
            let rows = grouped[kind] ?? []
            let title = Self.wikiTitle(for: kind)
            let body = rows.map { row in
                let name = row["title"] as? String ?? ""
                let value = row["value"] as? String ?? ""
                let hint = row["usage_hint"] as? String ?? ""
                return "## \(name)\n\n\(value)"
                    + (hint.isEmpty ? "" : "\n\n使用提示：\(hint)")
            }.joined(separator: "\n\n")
            let ids = rows.compactMap { $0["memory_id"] as? String }
            statements.append(
                (
                    """
                    INSERT INTO wiki_pages(
                      page_id, repo_id, slug, title, body, status,
                      source_memory_ids_json, source_episode_ids_json,
                      last_built_at, last_verified_at, created_at, updated_at
                    ) VALUES(?, ?, ?, ?, ?, 'generated', ?, '[]', ?, NULL, ?, ?);
                    """,
                    [
                        .text("wiki-\(repoID)-\(kind)"), .text(repoID), .text(kind),
                        .text(title), .text(body), .text(Self.jsonString(ids)),
                        .text(now), .text(now), .text(now),
                    ]
                )
            )
        }
        if !episodes.isEmpty {
            let body = episodes.map { row in
                let title = row["title"] as? String ?? ""
                let summary = row["summary"] as? String ?? ""
                let outcome = row["outcome"] as? String ?? ""
                return "## \(title)\n\n\(summary)"
                    + (outcome.isEmpty ? "" : "\n\n结果：\(outcome)")
            }.joined(separator: "\n\n")
            let ids = episodes.compactMap { $0["episode_id"] as? String }
            statements.append(
                (
                    """
                    INSERT INTO wiki_pages(
                      page_id, repo_id, slug, title, body, status,
                      source_memory_ids_json, source_episode_ids_json,
                      last_built_at, last_verified_at, created_at, updated_at
                    ) VALUES(?, ?, 'episodes', '项目经历', ?, 'generated',
                             '[]', ?, ?, NULL, ?, ?);
                    """,
                    [
                        .text("wiki-\(repoID)-episodes"), .text(repoID), .text(body),
                        .text(Self.jsonString(ids)), .text(now), .text(now), .text(now),
                    ]
                )
            )
        }
        try executeTransaction(statements)
        return try listWikiPages(repoRoot: repoRoot)
    }

    func rebuildSearchIndex(repoRoot: String) throws -> NativeIndexResult {
        let repoID = try ensureRepoID(repoRoot: repoRoot)
        var documents: [(id: String, type: String, ref: String, title: String, body: String)] = []
        for row in try query(
            """
            SELECT c.conversation_id,
                   COALESCE(c.summary, c.conversation_id) AS title,
                   COALESCE(group_concat(m.content, char(10)), '') AS body
            FROM conversations c
            LEFT JOIN messages m ON m.conversation_id = c.conversation_id
            WHERE c.repo_id = ?
            GROUP BY c.conversation_id;
            """,
            bindings: [.text(repoID)]
        ) {
            let ref = row["conversation_id"] as? String ?? ""
            documents.append((
                "conversation:\(ref)",
                "conversation",
                ref,
                row["title"] as? String ?? ref,
                row["body"] as? String ?? ""
            ))
        }
        for row in try query(
            """
            SELECT memory_id, title, value, usage_hint
            FROM approved_memories
            WHERE repo_id = ? AND status = 'active';
            """,
            bindings: [.text(repoID)]
        ) {
            let ref = row["memory_id"] as? String ?? ""
            documents.append((
                "memory:\(ref)",
                "memory",
                ref,
                row["title"] as? String ?? ref,
                "\(row["value"] as? String ?? "")\n\(row["usage_hint"] as? String ?? "")"
            ))
        }
        for row in try query(
            "SELECT page_id, title, body FROM wiki_pages WHERE repo_id = ?;",
            bindings: [.text(repoID)]
        ) {
            let ref = row["page_id"] as? String ?? ""
            documents.append((
                "wiki:\(ref)",
                "wiki",
                ref,
                row["title"] as? String ?? ref,
                row["body"] as? String ?? ""
            ))
        }
        let now = ISO8601DateFormatter().string(from: Date())
        let oldIDs = try query(
            "SELECT doc_id FROM search_documents WHERE repo_id = ?;",
            bindings: [.text(repoID)]
        ).compactMap { $0["doc_id"] as? String }
        var statements: [(String, [Binding])] = []
        for id in oldIDs {
            statements.append((
                "DELETE FROM search_documents_fts WHERE doc_id = ?;",
                [.text(id)]
            ))
        }
        statements.append((
            "DELETE FROM document_embeddings WHERE repo_id = ?;",
            [.text(repoID)]
        ))
        statements.append((
            "DELETE FROM search_documents WHERE repo_id = ?;",
            [.text(repoID)]
        ))
        for document in documents {
            statements.append((
                """
                INSERT INTO search_documents(
                  doc_id, repo_id, doc_type, doc_ref_id, title, body, updated_at
                ) VALUES(?, ?, ?, ?, ?, ?, ?);
                """,
                [
                    .text(document.id), .text(repoID), .text(document.type),
                    .text(document.ref), .text(document.title),
                    .text(document.body), .text(now),
                ]
            ))
            statements.append((
                "INSERT INTO search_documents_fts(doc_id, title, body) VALUES(?, ?, ?);",
                [.text(document.id), .text(document.title), .text(document.body)]
            ))
            statements.append((
                """
                INSERT INTO document_embeddings(
                  doc_id, repo_id, embedding_model, dimensions, vector_json, updated_at
                ) VALUES(?, ?, 'native-token-hash-v1', 128, ?, ?);
                """,
                [
                    .text(document.id), .text(repoID),
                    .text(Self.jsonString(Self.tokenHashVector(
                        document.title + "\n" + document.body
                    ))),
                    .text(now),
                ]
            ))
        }
        try executeTransaction(statements)
        return NativeIndexResult(
            documentCount: documents.count,
            embeddingCount: documents.count
        )
    }

    func mergeRepoAlias(repoRoot: String, aliasRoot: String) throws -> NativeAliasResult {
        let repoID = try ensureRepoID(repoRoot: repoRoot)
        let alias = aliasRoot.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !alias.isEmpty, alias != repoRoot else {
            throw NativeConversationStoreError.database("项目别名为空或与主路径相同。")
        }
        let now = ISO8601DateFormatter().string(from: Date())
        try executeTransaction([
            (
                """
                INSERT INTO repo_aliases(
                  alias_id, repo_id, alias_root, alias_kind,
                  confidence, created_at, updated_at
                ) VALUES(?, ?, ?, 'user', 1.0, ?, ?)
                ON CONFLICT(repo_id, alias_root) DO UPDATE SET
                  confidence = 1.0, updated_at = excluded.updated_at;
                """,
                [
                    .text(UUID().uuidString), .text(repoID), .text(alias),
                    .text(now), .text(now),
                ]
            ),
        ])
        return NativeAliasResult(repoID: repoID, repoRoot: repoRoot, aliasRoot: alias)
    }

    private func evidence(ownerType: String, ownerID: String) throws -> [[String: Any]] {
        try query(
            """
            SELECT evidence_id, conversation_id, message_id, tool_call_id,
                   file_change_id, excerpt
            FROM evidence_refs
            WHERE owner_type = ? AND owner_id = ?
            ORDER BY created_at, evidence_id;
            """,
            bindings: [.text(ownerType), .text(ownerID)]
        )
    }

    private func findRepoID(repoRoot: String) throws -> String? {
        try query(
            """
            SELECT repo_id
            FROM (
              SELECT repo_id, 0 AS priority FROM repos
                WHERE repo_root = ?
              UNION ALL
              SELECT repo_id, 1 AS priority FROM repo_aliases
                WHERE alias_root = ?
            )
            ORDER BY priority
            LIMIT 1;
            """,
            bindings: [.text(repoRoot), .text(repoRoot)]
        ).first?["repo_id"] as? String
    }

    private func ensureRepoID(repoRoot: String) throws -> String {
        if let existing = try findRepoID(repoRoot: repoRoot) { return existing }
        let repoID = "repo-" + SHA256.hash(data: Data(repoRoot.utf8))
            .map { String(format: "%02x", $0) }
            .joined()
        let now = ISO8601DateFormatter().string(from: Date())
        try executeTransaction([
            (
                """
                INSERT OR IGNORE INTO repos(
                  repo_id, repo_root, repo_fingerprint, git_remote,
                  default_branch, created_at, updated_at
                ) VALUES(?, ?, ?, NULL, NULL, ?, ?);
                """,
                [
                    .text(repoID), .text(repoRoot),
                    .text(String(repoID.dropFirst("repo-".count))),
                    .text(now), .text(now),
                ]
            ),
        ])
        return repoID
    }

    private func sourceCounts() throws -> [String: Int] {
        let rows = try query(
            """
            SELECT lower(source_agent) AS agent, COUNT(*) AS count
            FROM conversations
            GROUP BY lower(source_agent);
            """
        )
        return Dictionary(uniqueKeysWithValues: rows.compactMap { row in
            guard let agent = row["agent"] as? String else { return nil }
            return (agent, row["count"] as? Int ?? 0)
        })
    }

    private static func resumeCommand(agent: String, id: String) -> String? {
        switch agent.lowercased() {
        case "claude": "claude --resume \(id)"
        case "codex": "codex resume \(id)"
        case "gemini": "gemini --resume \(id)"
        case "zcode": "zcode --resume \(id)"
        case "hermes": "hermes resume \(id)"
        case "kimi": "kimi --session \(id)"
        default: nil
        }
    }

    /// Decodes stored JSON text back into a Foundation value.
    ///
    /// `.fragmentsAllowed` is mandatory. Tool inputs are very often top-level
    /// JSON strings — `exec` and `node_repl` style tools take a code blob, not
    /// an object — and without the option `JSONSerialization` rejects every one
    /// of them. The old code fell back to returning the raw text *including its
    /// surrounding quotes*, which the writer below then encoded a second time.
    /// Each read/write round trip therefore doubled the escaping until single
    /// rows reached hundreds of megabytes. See docs/TOOL_CALL_JSON_BLOAT.md.
    private static func jsonObject(from text: String) -> Any {
        guard let data = text.data(using: .utf8) else { return NSNull() }
        if let value = try? JSONSerialization.jsonObject(
            with: data,
            options: [.fragmentsAllowed]
        ) {
            return value
        }
        // Genuinely non-JSON legacy rows: hand back the text as a plain value so
        // it round-trips as a single encoded layer instead of accumulating one.
        return text
    }

    private static func jsonString(_ value: Any) -> String {
        guard let data = try? JSONSerialization.data(withJSONObject: value),
              let string = String(data: data, encoding: .utf8) else { return "[]" }
        return string
    }

    private static func wikiTitle(for kind: String) -> String {
        switch kind.lowercased() {
        case "command": "常用命令"
        case "convention": "项目约定"
        case "decision": "关键决策"
        case "gotcha": "注意事项"
        case "preference": "协作偏好"
        default: kind.capitalized
        }
    }

    private static func tokenHashVector(_ text: String) -> [Double] {
        var vector = Array(repeating: 0.0, count: 128)
        let tokens = text.lowercased().split { !$0.isLetter && !$0.isNumber }
        for token in tokens {
            let digest = Array(SHA256.hash(data: Data(token.utf8)))
            let index = Int(digest[0]) % vector.count
            let sign = digest[1] & 1 == 0 ? 1.0 : -1.0
            vector[index] += sign
        }
        let norm = sqrt(vector.reduce(0) { $0 + $1 * $1 })
        guard norm > 0 else { return vector }
        return vector.map { $0 / norm }
    }

    private func decodeRows<T: Decodable>(_ rows: [[String: Any]], as type: T.Type) throws -> T {
        let data = try JSONSerialization.data(withJSONObject: rows)
        return try decoder.decode(type, from: data)
    }

    private func decodeObject<T: Decodable>(_ object: [String: Any], as type: T.Type) throws -> T {
        let data = try JSONSerialization.data(withJSONObject: object)
        return try decoder.decode(type, from: data)
    }

    private enum Binding {
        case text(String)
        case integer(Int)
        case double(Double)
    }

    private func executeTransaction(_ statements: [(String, [Binding])]) throws {
        var connection: OpaquePointer?
        let flags = SQLITE_OPEN_READWRITE | SQLITE_OPEN_FULLMUTEX
        guard sqlite3_open_v2(databaseURL.path, &connection, flags, nil) == SQLITE_OK,
              let connection else {
            let message = connection.map { String(cString: sqlite3_errmsg($0)) }
                ?? "unknown SQLite error"
            if let connection { sqlite3_close(connection) }
            throw NativeConversationStoreError.database(message)
        }
        defer { sqlite3_close(connection) }
        sqlite3_busy_timeout(connection, 5_000)
        guard sqlite3_exec(connection, "BEGIN IMMEDIATE;", nil, nil, nil) == SQLITE_OK else {
            throw NativeConversationStoreError.database(
                String(cString: sqlite3_errmsg(connection))
            )
        }
        do {
            for (sql, bindings) in statements {
                try execute(connection: connection, sql: sql, bindings: bindings)
            }
            guard sqlite3_exec(connection, "COMMIT;", nil, nil, nil) == SQLITE_OK else {
                throw NativeConversationStoreError.database(
                    String(cString: sqlite3_errmsg(connection))
                )
            }
        } catch {
            sqlite3_exec(connection, "ROLLBACK;", nil, nil, nil)
            throw error
        }
    }

    private func execute(
        connection: OpaquePointer,
        sql: String,
        bindings: [Binding]
    ) throws {
        var statement: OpaquePointer?
        guard sqlite3_prepare_v2(connection, sql, -1, &statement, nil) == SQLITE_OK,
              let statement else {
            throw NativeConversationStoreError.database(
                String(cString: sqlite3_errmsg(connection))
            )
        }
        defer { sqlite3_finalize(statement) }
        for (offset, binding) in bindings.enumerated() {
            switch binding {
            case .text(let value):
                let result = value.withCString {
                    sqlite3_bind_text(statement, Int32(offset + 1), $0, -1, SQLITE_TRANSIENT)
                }
                guard result == SQLITE_OK else {
                    throw NativeConversationStoreError.database(
                        String(cString: sqlite3_errmsg(connection))
                    )
                }
            case .integer(let value):
                guard sqlite3_bind_int64(
                    statement,
                    Int32(offset + 1),
                    sqlite3_int64(value)
                ) == SQLITE_OK else {
                    throw NativeConversationStoreError.database(
                        String(cString: sqlite3_errmsg(connection))
                    )
                }
            case .double(let value):
                guard sqlite3_bind_double(
                    statement,
                    Int32(offset + 1),
                    value
                ) == SQLITE_OK else {
                    throw NativeConversationStoreError.database(
                        String(cString: sqlite3_errmsg(connection))
                    )
                }
            }
        }
        guard sqlite3_step(statement) == SQLITE_DONE else {
            throw NativeConversationStoreError.database(
                String(cString: sqlite3_errmsg(connection))
            )
        }
    }

    private func query(
        _ sql: String,
        bindings: [Binding] = []
    ) throws -> [[String: Any]] {
        let connection = try openQueryConnection()
        defer { sqlite3_close(connection) }
        return try query(connection: connection, sql: sql, bindings: bindings)
    }

    private func openQueryConnection() throws -> OpaquePointer {
        let path = databaseURL.standardizedFileURL.path
        guard FileManager.default.fileExists(atPath: path) else {
            throw NativeConversationStoreError.database(
                "数据库文件不存在：\(path)"
            )
        }
        let flags = SQLITE_OPEN_READWRITE | SQLITE_OPEN_FULLMUTEX
        var lastMessage = "unknown SQLite error"
        for attempt in 0..<3 {
            var connection: OpaquePointer?
            let result = sqlite3_open_v2(path, &connection, flags, nil)
            if result == SQLITE_OK, let connection {
                sqlite3_extended_result_codes(connection, 1)
                sqlite3_busy_timeout(connection, 5_000)
                return connection
            }
            if let connection {
                let primary = String(cString: sqlite3_errmsg(connection))
                let extended = sqlite3_extended_errcode(connection)
                lastMessage = "\(primary)（SQLite \(extended)，路径 \(path)）"
                sqlite3_close(connection)
            } else {
                lastMessage = "SQLite \(result)，路径 \(path)"
            }
            if attempt < 2 {
                Thread.sleep(forTimeInterval: 0.05 * Double(attempt + 1))
            }
        }
        throw NativeConversationStoreError.database(lastMessage)
    }

    private func query(
        connection: OpaquePointer,
        sql: String,
        bindings: [Binding] = []
    ) throws -> [[String: Any]] {
        var statement: OpaquePointer?
        guard sqlite3_prepare_v2(connection, sql, -1, &statement, nil) == SQLITE_OK,
              let statement else {
            throw NativeConversationStoreError.database(
                String(cString: sqlite3_errmsg(connection))
            )
        }
        defer { sqlite3_finalize(statement) }

        for (offset, binding) in bindings.enumerated() {
            switch binding {
            case .text(let value):
                let result = value.withCString {
                    sqlite3_bind_text(statement, Int32(offset + 1), $0, -1, SQLITE_TRANSIENT)
                }
                guard result == SQLITE_OK else {
                    throw NativeConversationStoreError.database(
                        String(cString: sqlite3_errmsg(connection))
                    )
                }
            case .integer(let value):
                guard sqlite3_bind_int64(
                    statement,
                    Int32(offset + 1),
                    sqlite3_int64(value)
                ) == SQLITE_OK else {
                    throw NativeConversationStoreError.database(
                        String(cString: sqlite3_errmsg(connection))
                    )
                }
            case .double(let value):
                guard sqlite3_bind_double(
                    statement,
                    Int32(offset + 1),
                    value
                ) == SQLITE_OK else {
                    throw NativeConversationStoreError.database(
                        String(cString: sqlite3_errmsg(connection))
                    )
                }
            }
        }

        var rows: [[String: Any]] = []
        while true {
            let result = sqlite3_step(statement)
            if result == SQLITE_DONE { break }
            guard result == SQLITE_ROW else {
                throw NativeConversationStoreError.database(
                    String(cString: sqlite3_errmsg(connection))
                )
            }
            var row: [String: Any] = [:]
            for index in 0..<sqlite3_column_count(statement) {
                guard let namePointer = sqlite3_column_name(statement, index) else { continue }
                let name = String(cString: namePointer)
                switch sqlite3_column_type(statement, index) {
                case SQLITE_INTEGER:
                    row[name] = Int(sqlite3_column_int64(statement, index))
                case SQLITE_FLOAT:
                    row[name] = sqlite3_column_double(statement, index)
                case SQLITE_TEXT:
                    if let value = sqlite3_column_text(statement, index) {
                        row[name] = String(cString: value)
                    }
                case SQLITE_NULL:
                    row[name] = NSNull()
                default:
                    break
                }
            }
            rows.append(row)
        }
        return rows
    }
}

enum NativeConversationStoreError: LocalizedError {
    case database(String)
    case notFound(agent: String, id: String)

    var errorDescription: String? {
        switch self {
        case .database(let message):
            "读取 AI Memory 数据库失败：\(message)"
        case .notFound(let agent, let id):
            "未找到 \(agent) 对话 \(id)。"
        }
    }
}

struct NativeIndexResult: Sendable {
    let documentCount: Int
    let embeddingCount: Int
}

struct NativeAliasResult: Sendable {
    let repoID: String
    let repoRoot: String
    let aliasRoot: String
}

private let SQLITE_TRANSIENT = unsafeBitCast(
    -1,
    to: sqlite3_destructor_type.self
)
