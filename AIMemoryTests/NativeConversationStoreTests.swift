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
