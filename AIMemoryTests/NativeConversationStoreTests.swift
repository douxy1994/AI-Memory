// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
import XCTest
import SQLite3
@testable import AIMemory

final class NativeConversationStoreTests: XCTestCase {
    func testListSearchAndReadUseIndependentDatabase() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("NativeConversationStoreTests-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: root) }
        let databaseURL = root.appendingPathComponent("aimemory.db")

        var database: NativeDatabase? = try NativeDatabase(url: databaseURL)
        _ = try await database?.currentSchemaVersion()
        database = nil

        var raw: OpaquePointer?
        XCTAssertEqual(sqlite3_open(databaseURL.path, &raw), SQLITE_OK)
        let seed = """
        INSERT INTO repos VALUES(
          'repo-1', '/tmp/native-project', 'fingerprint', NULL, NULL,
          '2026-07-23T10:00:00Z', '2026-07-23T10:00:00Z'
        );
        INSERT INTO conversations VALUES(
          'conversation-1', 'repo-1', 'codex', 'conversation-1',
          'Native SQLite search title', '2026-07-23T10:00:00Z',
          '2026-07-23T11:00:00Z', '/tmp/rollout.jsonl'
        );
        INSERT INTO messages VALUES(
          'message-1', 'conversation-1', 'user',
          'needle in message content', '2026-07-23T10:01:00Z'
        );
        INSERT INTO messages VALUES(
          'message-2', 'conversation-1', 'assistant',
          'Done', '2026-07-23T10:02:00Z'
        );
        INSERT INTO tool_calls VALUES(
          'tool-1', 'message-2', 'exec_command',
          '{"cmd":"pwd"}', 'ok', 'success'
        );
        INSERT INTO file_changes VALUES(
          'file-1', 'conversation-1', 'message-2',
          '/tmp/native-project/file.swift', 'modified',
          '2026-07-23T10:02:00Z'
        );
        """
        let seedResult = sqlite3_exec(raw, seed, nil, nil, nil)
        XCTAssertEqual(
            seedResult,
            SQLITE_OK,
            raw.map { String(cString: sqlite3_errmsg($0)) } ?? "no database"
        )
        sqlite3_close(raw)

        let store = NativeConversationStore(databaseURL: databaseURL)
        let list = try await store.listConversations(agent: "codex")
        XCTAssertEqual(list.count, 1)
        XCTAssertEqual(list[0].projectDir, "/tmp/native-project")
        XCTAssertEqual(list[0].messageCount, 2)
        XCTAssertEqual(list[0].fileCount, 1)

        let results = try await store.searchConversations(agent: "codex", text: "needle")
        XCTAssertEqual(results.map(\.id), ["conversation-1"])
        let otherAgentResults = try await store.searchConversations(
            agent: "claude",
            text: "needle"
        )
        XCTAssertTrue(otherAgentResults.isEmpty)

        let detail = try await store.readConversation(agent: "codex", id: "conversation-1")
        XCTAssertEqual(detail.messages.count, 2)
        XCTAssertEqual(detail.messages[1].toolCalls.first?.name, "exec_command")
        XCTAssertEqual(detail.messages[1].toolCalls.first?.input.preview, "{cmd: pwd}")
        XCTAssertEqual(detail.fileChanges.first?.path, "/tmp/native-project/file.swift")
        XCTAssertEqual(detail.resumeCommand, "codex resume conversation-1")
    }

    func testMemoryReviewLifecycleIsTransactional() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("NativeConversationStoreTests-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: root) }
        let databaseURL = root.appendingPathComponent("aimemory.db")
        var database: NativeDatabase? = try NativeDatabase(url: databaseURL)
        _ = try await database?.currentSchemaVersion()
        database = nil

        var raw: OpaquePointer?
        XCTAssertEqual(sqlite3_open(databaseURL.path, &raw), SQLITE_OK)
        let seed = """
        INSERT INTO repos VALUES(
          'repo-1', '/tmp/native-project', 'fingerprint', NULL, NULL,
          '2026-07-23T10:00:00Z', '2026-07-23T10:00:00Z'
        );
        INSERT INTO memory_candidates VALUES(
          'candidate-1', 'repo-1', 'convention', 'Use native APIs',
          'Use Swift and Apple frameworks', 'Keeps the app native', 0.9,
          'codex', 'pending_review', '2026-07-23T10:00:00Z', NULL
        );
        """
        XCTAssertEqual(sqlite3_exec(raw, seed, nil, nil, nil), SQLITE_OK)
        sqlite3_close(raw)

        let store = NativeConversationStore(databaseURL: databaseURL)
        let initialCandidates = try await store.listMemoryCandidates(
            repoRoot: "/tmp/native-project"
        )
        XCTAssertEqual(initialCandidates.count, 1)
        try await store.reviewCandidate(
            id: "candidate-1",
            action: "approve",
            title: "Native only",
            value: "",
            usageHint: "Apply to UI and storage",
            targetMemoryID: ""
        )
        let memories = try await store.listApprovedMemories(repoRoot: "/tmp/native-project")
        XCTAssertEqual(memories.count, 1)
        XCTAssertEqual(memories[0].title, "Native only")
        XCTAssertEqual(memories[0].value, "Use Swift and Apple frameworks")
        XCTAssertEqual(memories[0].status, "active")

        try await store.retireMemory(id: memories[0].memoryID)
        let retired = try await store.listApprovedMemories(
            repoRoot: "/tmp/native-project"
        )
        XCTAssertEqual(retired[0].status, "retired")
        try await store.reverifyMemory(id: memories[0].memoryID)
        let verifiedList = try await store.listApprovedMemories(
            repoRoot: "/tmp/native-project"
        )
        let verified = verifiedList[0]
        XCTAssertEqual(verified.status, "active")
        XCTAssertEqual(verified.freshnessStatus, "fresh")
    }

    func testRunsConflictsEntityGraphAndCheckpointResumeMatchMCPDataSurfaces() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("NativeConversationStoreMCP-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: root) }
        let databaseURL = root.appendingPathComponent("aimemory.db")
        var database: NativeDatabase? = try NativeDatabase(url: databaseURL)
        _ = try await database?.currentSchemaVersion()
        database = nil

        var raw: OpaquePointer?
        XCTAssertEqual(sqlite3_open(databaseURL.path, &raw), SQLITE_OK)
        let seed = """
        INSERT INTO repos(repo_id, repo_root, repo_fingerprint, git_remote, default_branch,
                          created_at, updated_at)
        VALUES('repo-1', '/tmp/native-project', 'fingerprint', NULL, NULL,
               '2026-07-23T10:00:00Z', '2026-07-23T10:00:00Z');
        INSERT INTO conversations(
          conversation_id, repo_id, source_agent, source_conversation_id,
          summary, started_at, updated_at, storage_path
        ) VALUES(
          'conversation-1', 'repo-1', 'codex', 'conversation-1',
          'Implement native feature', '2026-07-23T10:00:00Z',
          '2026-07-23T11:00:00Z', '/tmp/rollout.jsonl'
        );
        INSERT INTO messages(message_id, conversation_id, role, content, timestamp)
        VALUES('message-1', 'conversation-1', 'assistant', 'done',
               '2026-07-23T10:01:00Z');
        INSERT INTO tool_calls(tool_call_id, message_id, name, input_json, output_text, status)
        VALUES('tool-1', 'message-1', 'exec_command', '{}', 'ok', 'success');
        INSERT INTO file_changes(
          file_change_id, conversation_id, message_id, path, change_type, timestamp
        ) VALUES(
          'file-1', 'conversation-1', 'message-1', '/tmp/native-project/App.swift',
          'modified', '2026-07-23T10:02:00Z'
        );
        INSERT INTO memory_candidates(
          candidate_id, repo_id, kind, summary, value, why_it_matters,
          confidence, proposed_by, status, created_at
        ) VALUES(
          'candidate-1', 'repo-1', 'rule', 'Native only', 'Use Swift',
          'Compatibility', 0.9, 'codex', 'pending_review', '2026-07-23T10:00:00Z'
        );
        INSERT INTO approved_memories(
          memory_id, repo_id, kind, title, value, usage_hint, status,
          last_verified_at, created_from_candidate_id, created_at, updated_at,
          freshness_status, freshness_score, verified_at, verified_by
        ) VALUES(
          'memory-1', 'repo-1', 'command', 'Build', 'xcodebuild', '',
          'active', '2026-07-23T10:00:00Z', NULL,
          '2026-07-23T10:00:00Z', '2026-07-23T10:00:00Z',
          'fresh', 1.0, '2026-07-23T10:00:00Z', 'test'
        );
        INSERT INTO memory_conflicts(
          conflict_id, repo_id, candidate_id, memory_id, reason, status, created_at
        ) VALUES(
          'conflict-1', 'repo-1', 'candidate-1', 'memory-1',
          'values differ', 'open', '2026-07-23T10:00:00Z'
        );
        INSERT INTO memory_entities(
          entity_id, repo_id, name, normalized_name, kind, created_at, updated_at
        ) VALUES(
          'entity-1', 'repo-1', 'SwiftUI', 'swiftui', 'framework',
          '2026-07-23T10:00:00Z', '2026-07-23T10:00:00Z'
        );
        INSERT INTO memory_entity_links(
          link_id, repo_id, entity_id, owner_type, owner_id, relationship, created_at
        ) VALUES(
          'link-1', 'repo-1', 'entity-1', 'memory', 'memory-1',
          'mentions', '2026-07-23T10:00:00Z'
        );
        INSERT INTO conversation_chunks(
          chunk_id, repo_id, conversation_id, chunk_type, title, body,
          message_ids_json, ordinal, token_estimate, created_at, updated_at
        ) VALUES(
          'chunk-1', 'repo-1', 'conversation-1', 'message_range',
          'Conversation excerpt', 'SwiftUI implementation details',
          '["message-1"]', 0, 8,
          '2026-07-23T10:00:00Z', '2026-07-23T10:00:00Z'
        );
        INSERT INTO memory_entity_links(
          link_id, repo_id, entity_id, owner_type, owner_id, relationship, created_at
        ) VALUES(
          'link-2', 'repo-1', 'entity-1', 'chunk', 'chunk-1',
          'mentions', '2026-07-23T10:01:00Z'
        );
        INSERT INTO checkpoints(
          checkpoint_id, repo_id, conversation_id, source_agent, status,
          summary, resume_command, metadata_json, handoff_id, created_at
        ) VALUES(
          'checkpoint-1', 'repo-1', 'conversation-1', 'codex', 'active',
          'Continue native implementation', 'codex resume conversation-1',
          '{}', NULL, '2026-07-23T10:00:00Z'
        );
        """
        let mcpSeedResult = sqlite3_exec(raw, seed, nil, nil, nil)
        XCTAssertEqual(
            mcpSeedResult,
            SQLITE_OK,
            raw.map { String(cString: sqlite3_errmsg($0)) } ?? "no database"
        )
        sqlite3_close(raw)

        let store = NativeConversationStore(databaseURL: databaseURL)
        let runs = try await store.listActiveRuns(repoRoot: "/tmp/native-project")
        XCTAssertEqual(runs.count, 1)
        XCTAssertEqual(runs.first?.status, "waiting_for_review")
        XCTAssertEqual(runs.first?.artifactCount, 2)
        let artifacts = try await store.listRunArtifacts(
            repoRoot: "/tmp/native-project"
        )
        XCTAssertEqual(artifacts.count, 2)
        let conflicts = try await store.listMemoryConflicts(
            repoRoot: "/tmp/native-project",
            status: "open"
        )
        XCTAssertEqual(conflicts.first?.memoryTitle, "Build")
        let graph = try await store.listEntityGraph(
            repoRoot: "/tmp/native-project",
            limit: 25
        )
        XCTAssertEqual(graph.entities.first?.name, "SwiftUI")
        let chunkLink = graph.links.first { $0.ownerType == "chunk" }
        XCTAssertEqual(chunkLink?.sourceTitle, "Conversation excerpt")
        XCTAssertEqual(chunkLink?.sourceConversationID, "conversation-1")

        let handoff = try await store.resumeFromCheckpoint(
            checkpointID: "checkpoint-1",
            toAgent: "claude",
            targetProfile: "reviewer"
        )
        XCTAssertEqual(handoff.checkpointID, "checkpoint-1")
        XCTAssertEqual(handoff.fromAgent, "codex")
        XCTAssertEqual(handoff.toAgent, "claude")
        await XCTAssertThrowsErrorAsync {
            _ = try await store.resumeFromCheckpoint(
                checkpointID: "checkpoint-1",
                toAgent: "gemini",
                targetProfile: nil
            )
        }
    }

    func testAutomaticCheckpointUpsertKeepsOneRecoveryPointPerConversation() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("NativeAutoCapture-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: root) }
        let databaseURL = root.appendingPathComponent("aimemory.db")
        var database: NativeDatabase? = try NativeDatabase(url: databaseURL)
        _ = try await database?.currentSchemaVersion()
        database = nil

        let store = NativeConversationStore(databaseURL: databaseURL)
        var raw: OpaquePointer?
        XCTAssertEqual(sqlite3_open(databaseURL.path, &raw), SQLITE_OK)
        XCTAssertEqual(
            sqlite3_exec(
                raw,
                """
                INSERT INTO repos(
                  repo_id, repo_root, repo_fingerprint, git_remote,
                  default_branch, created_at, updated_at
                ) VALUES(
                  'legacy-repo-id', '/tmp/native-project', 'legacy',
                  NULL, NULL, '2026-07-23T10:00:00Z',
                  '2026-07-23T10:00:00Z'
                );
                """,
                nil,
                nil,
                nil
            ),
            SQLITE_OK
        )
        sqlite3_close(raw)
        try await store.upsertConversation(
            ConversationDetail(
                id: "conversation-1",
                sourceAgent: "codex",
                projectDir: "/tmp/native-project",
                createdAt: "2026-07-23T10:00:00Z",
                updatedAt: "2026-07-23T10:01:00Z",
                summary: "Initial",
                storagePath: "/tmp/rollout.jsonl",
                resumeCommand: "codex resume conversation-1",
                messages: [],
                fileChanges: []
            )
        )
        let first = try await store.upsertAutoCheckpoint(
            repoRoot: "/tmp/native-project",
            conversationID: "codex:conversation-1",
            sourceAgent: "codex",
            summary: "Initial",
            resumeCommand: "codex resume conversation-1",
            metadataJSON: #"{"capture":"auto","message_count":1}"#
        )
        let second = try await store.upsertAutoCheckpoint(
            repoRoot: "/tmp/native-project",
            conversationID: "codex:conversation-1",
            sourceAgent: "codex",
            summary: "Updated",
            resumeCommand: "codex resume conversation-1",
            metadataJSON: #"{"capture":"auto","message_count":3}"#
        )

        XCTAssertEqual(first.checkpointID, second.checkpointID)
        let checkpoints = try await store.listCheckpoints(
            repoRoot: "/tmp/native-project"
        )
        XCTAssertEqual(checkpoints.count, 1)
        XCTAssertEqual(checkpoints.first?.summary, "Updated")
        XCTAssertEqual(checkpoints.first?.messageCount, 3)
    }

    // MARK: - tool_calls.input_json recursive escaping
    // See docs/TOOL_CALL_JSON_BLOAT.md

    /// Core regression. Tool inputs are frequently top-level JSON strings —
    /// `exec` and `node_repl` take a code blob, not an object. Before the fix
    /// `JSONSerialization` rejected those fragments, the reader returned the
    /// raw quoted text, and the writer encoded it again, doubling the value on
    /// every round trip until single rows reached 364 MB. The stored length
    /// must now stay constant.
    func testTopLevelJSONStringToolInputDoesNotGrowAcrossRoundTrips() async throws {
        let databaseURL = try await Self.makeSeededDatabase(
            inputJSON: #"'"const x = 1;\nconsole.log(\"hi\");"'"#
        )
        defer { try? FileManager.default.removeItem(
            at: databaseURL.deletingLastPathComponent()
        ) }

        let store = NativeConversationStore(databaseURL: databaseURL)
        var lengths: [Int] = [try Self.storedInputLength(databaseURL)]
        for _ in 0..<5 {
            let detail = try await store.readConversationByID("conversation-1")
            try await store.upsertConversation(detail)
            lengths.append(try Self.storedInputLength(databaseURL))
        }

        XCTAssertEqual(
            Set(lengths).count,
            1,
            "input_json 长度在往返中发生变化，递归转义已回归：\(lengths)"
        )
    }

    /// Numbers, booleans and null are top-level fragments too, and hit exactly
    /// the same rejected-fragment path as strings did.
    func testAllJSONFragmentKindsRoundTripWithoutGrowing() async throws {
        for literal in [#"'42'"#, #"'true'"#, #"'null'"#, #"'"plain"'"#] {
            let databaseURL = try await Self.makeSeededDatabase(inputJSON: literal)
            defer { try? FileManager.default.removeItem(
                at: databaseURL.deletingLastPathComponent()
            ) }

            let store = NativeConversationStore(databaseURL: databaseURL)
            let before = try Self.storedInputLength(databaseURL)
            let detail = try await store.readConversationByID("conversation-1")
            try await store.upsertConversation(detail)
            let after = try Self.storedInputLength(databaseURL)

            XCTAssertEqual(before, after, "字面量 \(literal) 在往返后长度改变")
        }
    }

    /// The unwrap helper must collapse accumulated layers and then be a no-op.
    func testUnwrapNestedJSONTextCollapsesLayersAndIsIdempotent() throws {
        let payload = #""payload""#
        var bloated = payload
        for _ in 0..<20 {
            let data = try JSONSerialization.data(
                withJSONObject: bloated,
                options: [.fragmentsAllowed]
            )
            bloated = String(data: data, encoding: .utf8)!
        }
        XCTAssertGreaterThan(bloated.count, 100_000)

        let once = NativeDatabase.unwrapNestedJSONText(bloated)
        XCTAssertEqual(once, payload)
        // Already-correct values report "nothing to do" rather than rewriting.
        XCTAssertNil(NativeDatabase.unwrapNestedJSONText(payload))
        XCTAssertNil(NativeDatabase.unwrapNestedJSONText(#"{"cmd":"pwd"}"#))
    }

    /// Schema 2 must repair rows damaged by older builds when the database is
    /// opened, without touching rows that are already correct.
    func testMigrationRepairsBloatedToolInputOnOpen() async throws {
        let databaseURL = try await Self.makeSeededDatabase(inputJSON: #"'{"cmd":"pwd"}'"#)
        defer { try? FileManager.default.removeItem(
            at: databaseURL.deletingLastPathComponent()
        ) }

        var bloated = #""echo hi""#
        for _ in 0..<12 {
            let data = try JSONSerialization.data(
                withJSONObject: bloated,
                options: [.fragmentsAllowed]
            )
            bloated = String(data: data, encoding: .utf8)!
        }

        var raw: OpaquePointer?
        XCTAssertEqual(sqlite3_open(databaseURL.path, &raw), SQLITE_OK)
        var insert: OpaquePointer?
        XCTAssertEqual(
            sqlite3_prepare_v2(
                raw,
                """
                INSERT INTO tool_calls(
                  tool_call_id, message_id, name, input_json, output_text, status
                ) VALUES('tool-bloated', 'message-2', 'exec', ?, 'ok', 'success');
                """,
                -1,
                &insert,
                nil
            ),
            SQLITE_OK
        )
        sqlite3_bind_text(insert, 1, bloated, -1, unsafeBitCast(
            -1,
            to: sqlite3_destructor_type.self
        ))
        XCTAssertEqual(sqlite3_step(insert), SQLITE_DONE)
        sqlite3_finalize(insert)
        // Force the migration to run again over the damaged row.
        XCTAssertEqual(
            sqlite3_exec(raw, "PRAGMA user_version = 1;", nil, nil, nil),
            SQLITE_OK
        )
        sqlite3_close(raw)

        var database: NativeDatabase? = try NativeDatabase(
            url: databaseURL,
            createMigrationBackup: false
        )
        let version = try await database?.currentSchemaVersion()
        XCTAssertEqual(version, 2)
        database = nil

        XCTAssertEqual(
            try Self.storedInput(databaseURL, toolCallID: "tool-bloated"),
            #""echo hi""#
        )
        // The healthy row must be byte-identical afterwards.
        XCTAssertEqual(
            try Self.storedInput(databaseURL, toolCallID: "tool-1"),
            #"{"cmd":"pwd"}"#
        )
    }

    /// A single tool input past the cap is truncated with a diagnosable marker
    /// instead of being persisted whole.
    func testOversizedToolInputIsTruncatedBeforeInsert() async throws {
        let databaseURL = try await Self.makeSeededDatabase(inputJSON: #"'{"cmd":"pwd"}'"#)
        defer { try? FileManager.default.removeItem(
            at: databaseURL.deletingLastPathComponent()
        ) }

        let store = NativeConversationStore(databaseURL: databaseURL)
        let detail = try await store.readConversationByID("conversation-1")
        let huge = String(repeating: "x", count: 4_000_000)
        let target = detail.messages[1]
        let original = target.toolCalls[0]
        let patchedMessage = ConversationMessage(
            id: target.id,
            timestamp: target.timestamp,
            role: target.role,
            content: target.content,
            toolCalls: [
                ToolCall(
                    id: original.id,
                    name: original.name,
                    input: .string(huge),
                    output: original.output,
                    status: original.status
                )
            ],
            metadata: target.metadata
        )
        let patched = ConversationDetail(
            id: detail.id,
            sourceAgent: detail.sourceAgent,
            projectDir: detail.projectDir,
            createdAt: detail.createdAt,
            updatedAt: detail.updatedAt,
            summary: detail.summary,
            storagePath: detail.storagePath,
            resumeCommand: detail.resumeCommand,
            messages: [detail.messages[0], patchedMessage],
            fileChanges: detail.fileChanges
        )
        try await store.upsertConversation(patched)

        let stored = try Self.storedInput(databaseURL, toolCallID: original.id)
        XCTAssertLessThan(stored.utf8.count, NativeConversationStore.maxToolInputBytes + 4_096)
        XCTAssertTrue(stored.contains("_truncated"))
        XCTAssertTrue(stored.contains("_original_bytes"))
    }

    // MARK: - helpers

    private static func makeSeededDatabase(inputJSON: String) async throws -> URL {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("ToolInputBloat-\(UUID().uuidString)")
        try FileManager.default.createDirectory(
            at: root,
            withIntermediateDirectories: true
        )
        let databaseURL = root.appendingPathComponent("aimemory.db")

        var database: NativeDatabase? = try NativeDatabase(url: databaseURL)
        _ = try await database?.currentSchemaVersion()
        database = nil

        var raw: OpaquePointer?
        guard sqlite3_open(databaseURL.path, &raw) == SQLITE_OK else {
            throw NativeDatabaseError.openFailed("seed open failed")
        }
        let seed = """
        INSERT INTO repos VALUES(
          'repo-1', '/tmp/native-project', 'fingerprint', NULL, NULL,
          '2026-07-23T10:00:00Z', '2026-07-23T10:00:00Z'
        );
        INSERT INTO conversations VALUES(
          'conversation-1', 'repo-1', 'codex', 'conversation-1',
          'Bloat regression', '2026-07-23T10:00:00Z',
          '2026-07-23T11:00:00Z', '/tmp/rollout.jsonl'
        );
        INSERT INTO messages VALUES(
          'message-1', 'conversation-1', 'user',
          'run it', '2026-07-23T10:01:00Z'
        );
        INSERT INTO messages VALUES(
          'message-2', 'conversation-1', 'assistant',
          'Done', '2026-07-23T10:02:00Z'
        );
        INSERT INTO tool_calls VALUES(
          'tool-1', 'message-2', 'exec', \(inputJSON), 'ok', 'success'
        );
        """
        let result = sqlite3_exec(raw, seed, nil, nil, nil)
        let message = raw.map { String(cString: sqlite3_errmsg($0)) } ?? "no database"
        sqlite3_close(raw)
        guard result == SQLITE_OK else {
            throw NativeDatabaseError.statementFailed(message)
        }
        return databaseURL
    }

    private static func storedInput(
        _ databaseURL: URL,
        toolCallID: String
    ) throws -> String {
        var raw: OpaquePointer?
        guard sqlite3_open(databaseURL.path, &raw) == SQLITE_OK else {
            throw NativeDatabaseError.openFailed("read open failed")
        }
        defer { sqlite3_close(raw) }
        var statement: OpaquePointer?
        guard sqlite3_prepare_v2(
            raw,
            "SELECT input_json FROM tool_calls WHERE tool_call_id = ?;",
            -1,
            &statement,
            nil
        ) == SQLITE_OK else {
            throw NativeDatabaseError.statementFailed("prepare failed")
        }
        defer { sqlite3_finalize(statement) }
        sqlite3_bind_text(statement, 1, toolCallID, -1, unsafeBitCast(
            -1,
            to: sqlite3_destructor_type.self
        ))
        guard sqlite3_step(statement) == SQLITE_ROW,
              let value = sqlite3_column_text(statement, 0) else {
            throw NativeDatabaseError.statementFailed("row not found")
        }
        return String(cString: value)
    }

    private static func storedInputLength(_ databaseURL: URL) throws -> Int {
        try storedInput(databaseURL, toolCallID: "tool-1").utf8.count
    }
}

private func XCTAssertThrowsErrorAsync(
    _ expression: () async throws -> Void,
    file: StaticString = #filePath,
    line: UInt = #line
) async {
    do {
        try await expression()
        XCTFail("Expected error to be thrown", file: file, line: line)
    } catch {}
}
