// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
import SQLite3
import XCTest
@testable import AIMemory

final class NativeMigrationServiceTests: XCTestCase {
    func testCopyWritesRealClaudeStoreAndReimportsForVerification() async throws {
        let fixture = try await Fixture()
        defer { fixture.remove() }
        let service = fixture.service
        try await fixture.conversations.upsertConversation(
            source(id: "copy-source", agent: "codex")
        )

        let copied = try await service.migrate(
            source: "codex",
            target: "claude",
            id: "copy-source",
            mode: "copy"
        )

        XCTAssertTrue(copied.verified)
        XCTAssertFalse(copied.cutDeletedSource)
        XCTAssertEqual(copied.sourceMessageCount, copied.targetMessageCount)
        XCTAssertTrue(
            FileManager.default.fileExists(
                atPath: fixture.root
                    .appendingPathComponent(".claude/projects")
                    .path
            )
        )
        let target = try await fixture.conversations.readConversation(
            agent: "claude",
            id: copied.newID
        )
        XCTAssertEqual(target.messages.first?.content, "keep this first user message")
    }

    func testCutArchivesOpenCodeSourceOnlyAfterGeminiVerification() async throws {
        let fixture = try await Fixture()
        defer { fixture.remove() }
        try fixture.makeOpenCodeStore()
        let original = source(id: "ses_source", agent: "opencode")
        try await fixture.conversations.upsertConversation(original)
        try fixture.insertOpenCodeSource(original)

        let cut = try await fixture.service.migrate(
            source: "opencode",
            target: "gemini",
            id: "ses_source",
            mode: "cut"
        )

        XCTAssertTrue(cut.verified)
        XCTAssertTrue(cut.cutDeletedSource)
        XCTAssertEqual(try fixture.openCodeArchived(id: "ses_source"), true)
        let sourceRows = try await fixture.conversations.listConversations(agent: "opencode")
        XCTAssertFalse(sourceRows.contains { $0.id == "ses_source" })
        let trashRecords = try await fixture.trash.list()
        XCTAssertEqual(trashRecords.count, 1)
    }

    func testCopyWritesRealCodexStoreAndThreadIndex() async throws {
        let fixture = try await Fixture()
        defer { fixture.remove() }
        try await fixture.conversations.upsertConversation(
            source(id: "claude-source", agent: "claude")
        )

        let copied = try await fixture.service.migrate(
            source: "claude",
            target: "codex",
            id: "claude-source",
            mode: "copy"
        )

        XCTAssertTrue(copied.verified)
        XCTAssertTrue(
            FileManager.default.fileExists(
                atPath: fixture.root.appendingPathComponent(".codex/state_5.sqlite").path
            )
        )
        let target = try await fixture.conversations.readConversation(
            agent: "codex",
            id: copied.newID
        )
        XCTAssertEqual(target.messages.count, 2)
        XCTAssertEqual(target.resumeCommand, "codex resume \(copied.newID)")
    }

    func testCopyWritesRealOpenCodeDatabase() async throws {
        let fixture = try await Fixture()
        defer { fixture.remove() }
        try fixture.makeOpenCodeStore()
        try await fixture.conversations.upsertConversation(
            source(id: "gemini-source", agent: "gemini")
        )

        let copied = try await fixture.service.migrate(
            source: "gemini",
            target: "opencode",
            id: "gemini-source",
            mode: "copy"
        )

        XCTAssertTrue(copied.verified)
        let target = try await fixture.conversations.readConversation(
            agent: "opencode",
            id: copied.newID
        )
        XCTAssertEqual(target.messages.map(\.content), [
            "keep this first user message",
            "kept",
        ])
    }

    func testUnsupportedTargetFailsBeforeAnyWrite() async throws {
        let fixture = try await Fixture()
        defer { fixture.remove() }
        try await fixture.conversations.upsertConversation(
            source(id: "source", agent: "codex")
        )
        do {
            _ = try await fixture.service.migrate(
                source: "codex",
                target: "hermes",
                id: "source",
                mode: "copy"
            )
            XCTFail("Expected unsupported target")
        } catch let error as NativeMigrationError {
            guard case .unsupportedTarget("hermes") = error else {
                return XCTFail("Unexpected error: \(error)")
            }
        }
    }

    private func source(id: String, agent: String) -> ConversationDetail {
        ConversationDetail(
            id: id,
            sourceAgent: agent,
            projectDir: "/tmp/migration-project",
            createdAt: "2026-07-23T10:00:00Z",
            updatedAt: "2026-07-23T11:00:00Z",
            summary: "Migration source",
            storagePath: agent == "opencode" ? "opencode.db" : nil,
            resumeCommand: nil,
            messages: [
                ConversationMessage(
                    id: "\(id)-user",
                    timestamp: "2026-07-23T10:00:00Z",
                    role: "user",
                    content: "keep this first user message",
                    toolCalls: [],
                    metadata: [:]
                ),
                ConversationMessage(
                    id: "\(id)-assistant",
                    timestamp: "2026-07-23T10:01:00Z",
                    role: "assistant",
                    content: "kept",
                    toolCalls: [],
                    metadata: [:]
                ),
            ],
            fileChanges: []
        )
    }
}

private final class Fixture {
    let root: URL
    let databaseURL: URL
    let conversations: NativeConversationStore
    let trash: NativeTrashStore
    let service: NativeMigrationService

    init() async throws {
        root = FileManager.default.temporaryDirectory
            .appendingPathComponent("NativeMigrationServiceTests-\(UUID().uuidString)")
        databaseURL = root.appendingPathComponent("app/aimemory.db")
        let database = try NativeDatabase(url: databaseURL)
        _ = try await database.currentSchemaVersion()
        conversations = NativeConversationStore(databaseURL: databaseURL)
        trash = NativeTrashStore(
            root: root.appendingPathComponent("trash"),
            conversations: conversations
        )
        let writer = NativeAgentConversationWriter(home: root)
        let importer = NativeHistoryImporter(store: conversations, home: root)
        service = NativeMigrationService(
            conversations: conversations,
            trash: trash,
            writer: writer,
            importer: importer,
            home: root
        )
    }

    func remove() {
        try? FileManager.default.removeItem(at: root)
    }

    func makeOpenCodeStore() throws {
        let url = root.appendingPathComponent(".local/share/opencode/opencode.db")
        try FileManager.default.createDirectory(
            at: url.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        try withDatabase(url) { database in
            try exec(
                database,
                """
                CREATE TABLE project (
                  id TEXT PRIMARY KEY, worktree TEXT, vcs TEXT, name TEXT,
                  time_created INTEGER, time_updated INTEGER, sandboxes TEXT
                );
                CREATE TABLE session (
                  id TEXT PRIMARY KEY, project_id TEXT, slug TEXT, directory TEXT,
                  title TEXT, version TEXT, summary_files INTEGER,
                  time_created INTEGER, time_updated INTEGER, time_archived INTEGER
                );
                CREATE TABLE message (
                  id TEXT PRIMARY KEY, session_id TEXT, time_created INTEGER,
                  time_updated INTEGER, data TEXT
                );
                CREATE TABLE part (
                  id TEXT PRIMARY KEY, message_id TEXT, session_id TEXT,
                  time_created INTEGER, time_updated INTEGER, data TEXT
                );
                """
            )
        }
    }

    func insertOpenCodeSource(_ conversation: ConversationDetail) throws {
        let url = root.appendingPathComponent(".local/share/opencode/opencode.db")
        try withDatabase(url) { database in
            try exec(
                database,
                """
                INSERT INTO project VALUES (
                  'project_source', '/tmp/migration-project', 'git', 'migration-project',
                  1784800800000, 1784804400000, '[]'
                );
                INSERT INTO session VALUES (
                  'ses_source', 'project_source', 'source', '/tmp/migration-project',
                  'Migration source', '1.0.0', 0, 1784800800000, 1784804400000, NULL
                );
                INSERT INTO message VALUES (
                  'msg_user', 'ses_source', 1784800800000, 1784800800000,
                  '{"role":"user","time":{"created":1784800800000}}'
                );
                INSERT INTO part VALUES (
                  'part_user', 'msg_user', 'ses_source', 1784800800000, 1784800800000,
                  '{"type":"text","text":"keep this first user message"}'
                );
                INSERT INTO message VALUES (
                  'msg_assistant', 'ses_source', 1784800860000, 1784800860000,
                  '{"role":"assistant","time":{"created":1784800860000}}'
                );
                INSERT INTO part VALUES (
                  'part_assistant', 'msg_assistant', 'ses_source', 1784800860000, 1784800860000,
                  '{"type":"text","text":"kept"}'
                );
                """
            )
        }
    }

    func openCodeArchived(id: String) throws -> Bool {
        let url = root.appendingPathComponent(".local/share/opencode/opencode.db")
        return try withDatabase(url) { database in
            var statement: OpaquePointer?
            sqlite3_prepare_v2(
                database,
                "SELECT time_archived FROM session WHERE id = '\(id)';",
                -1,
                &statement,
                nil
            )
            defer { sqlite3_finalize(statement) }
            guard sqlite3_step(statement) == SQLITE_ROW else { return false }
            return sqlite3_column_type(statement, 0) == SQLITE_INTEGER
        }
    }

    private func withDatabase<T>(
        _ url: URL,
        body: (OpaquePointer) throws -> T
    ) throws -> T {
        var database: OpaquePointer?
        guard sqlite3_open(url.path, &database) == SQLITE_OK, let database else {
            throw NSError(domain: "Fixture", code: 1)
        }
        defer { sqlite3_close(database) }
        return try body(database)
    }

    private func exec(_ database: OpaquePointer, _ sql: String) throws {
        guard sqlite3_exec(database, sql, nil, nil, nil) == SQLITE_OK else {
            throw NSError(
                domain: "Fixture",
                code: 2,
                userInfo: [
                    NSLocalizedDescriptionKey: String(cString: sqlite3_errmsg(database))
                ]
            )
        }
    }
}
