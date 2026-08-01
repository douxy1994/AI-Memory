// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
import XCTest
@testable import AIMemory

final class AIMemoryTests: XCTestCase {

    func testAgentKindLabels() {
        XCTAssertEqual(AgentKind.claude.label, "Claude")
        XCTAssertEqual(AgentKind.kimi.label, "Kimi Code")
        XCTAssertEqual(AgentKind.antigravity.label, "Antigravity")
    }

    func testConversationSummaryDecoding() throws {
        let json = """
        {
            "id": "abc-123",
            "source_agent": "codex",
            "project_dir": "/tmp/proj",
            "created_at": "2026-01-01T00:00:00+00:00",
            "updated_at": "2026-01-02T00:00:00+00:00",
            "summary": "A test conversation",
            "message_count": 5,
            "file_count": 2
        }
        """.data(using: .utf8)!

        let summary = try JSONDecoder().decode(ConversationSummary.self, from: json)
        XCTAssertEqual(summary.id, "abc-123")
        XCTAssertEqual(summary.agentKind, .codex)
        XCTAssertEqual(summary.messageCount, 5)
        XCTAssertEqual(summary.displayTitle, "A test conversation")
        XCTAssertEqual(summary.projectLeaf, "proj")
    }

    func testRepoHealthLenientDecoding() throws {
        let json = """
        {
            "repo_root": "/tmp/proj",
            "approved_memory_count": 3,
            "pending_candidate_count": 2,
            "indexed_chunk_count": 10,
            "search_document_count": 12,
            "latest_scan": {
                "scanned_conversation_count": 207,
                "linked_conversation_count": 5,
                "skipped_conversation_count": 202,
                "unmatched_project_roots": [
                    {"source_agent": "codex", "project_root": "/old", "conversation_count": 3}
                ]
            }
        }
        """.data(using: .utf8)!

        let health = try JSONDecoder().decode(RepoHealth.self, from: json)
        XCTAssertEqual(health.approvedMemoryCount, 3)
        XCTAssertEqual(health.latestScan?.scannedConversationCount, 207)
        XCTAssertEqual(health.latestScan?.unmatchedProjectRoots?.first?.conversationCount, 3)
    }

    func testJSONValueLenient() throws {
        let json = "{\"k\": [1, \"two\", null, {\"nested\": true}]}".data(using: .utf8)!
        let value = try JSONDecoder().decode(JSONValue.self, from: json)
        if case .object(let dict) = value, case .array(let arr) = dict["k"] {
            XCTAssertEqual(arr.count, 4)
        } else {
            XCTFail("unexpected JSONValue shape")
        }
    }

    func testDataPathsAreIndependent() {
        // AI Memory must never resolve to a ChatMem path.
        XCTAssertFalse(DataPaths.dbURL.path.contains("/ChatMem/"))
        XCTAssertFalse(DataPaths.dbURL.path.contains("/Chatmem/"))
        XCTAssertTrue(DataPaths.dbURL.path.contains("AIMemory"))
        XCTAssertEqual(DataPaths.keychainService, "com.aimemory.app.webdav")
        XCTAssertEqual(DataPaths.subsystem, "com.aimemory.app")
    }

    func testMemoryCandidateDecoding() throws {
        let json = """
        {
            "candidate_id": "c1",
            "kind": "gotcha",
            "summary": "Always run tests",
            "value": "Always run npm test before commit",
            "why_it_matters": "从明确标记提取",
            "confidence": 0.85,
            "proposed_by": "auto_extractor",
            "status": "pending_review",
            "created_at": "2026-07-19T00:00:00+00:00",
            "evidence_refs": [{"excerpt": "Remember: always run tests"}],
            "merge_suggestion": null,
            "conflict_suggestion": null
        }
        """.data(using: .utf8)!
        let c = try JSONDecoder().decode(MemoryCandidate.self, from: json)
        XCTAssertEqual(c.id, "c1")
        XCTAssertEqual(c.kindLabel, "注意事项")
        XCTAssertTrue(c.isActionable)
        XCTAssertEqual(c.evidenceRefs.first?.excerpt, "Remember: always run tests")
    }

    func testCheckpointMetadataParse() throws {
        let json = """
        {
            "checkpoint_id": "cp1",
            "conversation_id": "codex:abc",
            "source_agent": "codex",
            "status": "active",
            "summary": "Working on X",
            "resume_command": "codex resume abc",
            "metadata_json": "{\\"message_count\\": 42, \\"file_count\\": 3}",
            "created_at": "2026-07-19T00:00:00+00:00"
        }
        """.data(using: .utf8)!
        let cp = try JSONDecoder().decode(Checkpoint.self, from: json)
        XCTAssertEqual(cp.id, "cp1")
        XCTAssertEqual(cp.agentKind, .codex)
        XCTAssertEqual(cp.messageCount, 42)
    }

    func testHandoffDecoding() throws {
        let json = """
        {
            "handoff_id": "h1",
            "from_agent": "Claude macOS",
            "to_agent": "Codex Windows",
            "status": "draft",
            "current_goal": "Ship the thing",
            "done_items": ["a"],
            "next_items": ["b", "c"],
            "key_files": [],
            "useful_commands": []
        }
        """.data(using: .utf8)!
        let h = try JSONDecoder().decode(HandoffPacket.self, from: json)
        XCTAssertEqual(h.id, "h1")
        XCTAssertEqual(h.nextItems.count, 2)
    }
}
