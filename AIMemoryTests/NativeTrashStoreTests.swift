// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
import SQLite3
import XCTest
@testable import AIMemory

final class NativeTrashStoreTests: XCTestCase {
    func testTrashAndRestoreRoundTripKeepsConversationContent() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("NativeTrashStoreTests-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: root) }
        let databaseURL = root.appendingPathComponent("aimemory.db")
        var database: NativeDatabase? = try NativeDatabase(url: databaseURL)
        _ = try await database?.currentSchemaVersion()
        database = nil
        let conversations = NativeConversationStore(databaseURL: databaseURL)
        let detail = ConversationDetail(
            id: "trash-roundtrip",
            sourceAgent: "codex",
            projectDir: "/tmp/trash-project",
            createdAt: "2026-07-23T10:00:00Z",
            updatedAt: "2026-07-23T11:00:00Z",
            summary: "Trash roundtrip",
            storagePath: "/tmp/source.jsonl",
            resumeCommand: "codex resume trash-roundtrip",
            messages: [
                ConversationMessage(
                    id: "message-1",
                    timestamp: "2026-07-23T10:01:00Z",
                    role: "user",
                    content: "preserve me",
                    toolCalls: [],
                    metadata: [:]
                ),
            ],
            fileChanges: []
        )
        try await conversations.upsertConversation(detail)
        let trash = NativeTrashStore(
            root: root.appendingPathComponent("trash"),
            conversations: conversations
        )

        let result = try await trash.trash(
            agent: "codex",
            id: detail.id,
            retentionDays: 14
        )
        let afterTrash = try await conversations.listConversations(agent: "codex")
        XCTAssertTrue(afterTrash.isEmpty)
        let records = try await trash.list()
        XCTAssertEqual(records.count, 1)
        XCTAssertEqual(records[0].originalID, detail.id)
        XCTAssertFalse(records[0].recordPath.isEmpty)

        _ = try await trash.restore(trashID: result.trashID, agent: "codex")
        let afterRestore = try await trash.list()
        XCTAssertTrue(afterRestore.isEmpty)
        let restored = try await conversations.readConversation(
            agent: "codex",
            id: detail.id
        )
        XCTAssertEqual(restored.messages.first?.content, "preserve me")
    }

    func testFileBackedSourceIsBackedUpDeletedAndRestoredExactly() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("NativeTrashSourceTests-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: root) }
        let databaseURL = root.appendingPathComponent("app/aimemory.db")
        var database: NativeDatabase? = try NativeDatabase(url: databaseURL)
        _ = try await database?.currentSchemaVersion()
        database = nil
        let conversations = NativeConversationStore(databaseURL: databaseURL)
        let sourceURL = root.appendingPathComponent("claude/source.jsonl")
        try FileManager.default.createDirectory(
            at: sourceURL.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        let originalBytes = Data(#"{"type":"user","message":"exact source"}\n"#.utf8)
        try originalBytes.write(to: sourceURL)
        let detail = conversation(
            id: "file-backed",
            agent: "claude",
            storagePath: sourceURL.path
        )
        try await conversations.upsertConversation(detail)
        let trash = NativeTrashStore(
            root: root.appendingPathComponent("trash"),
            conversations: conversations,
            sourceWriter: NativeAgentConversationWriter(home: root)
        )

        let result = try await trash.trash(
            agent: detail.sourceAgent,
            id: detail.id,
            retentionDays: 14
        )

        XCTAssertFalse(FileManager.default.fileExists(atPath: sourceURL.path))
        let recordsAfterTrash = try await trash.list()
        let record = try XCTUnwrap(recordsAfterTrash.first)
        let recordData = try Data(contentsOf: URL(fileURLWithPath: record.recordPath))
        let recordObject = try XCTUnwrap(
            JSONSerialization.jsonObject(with: recordData) as? [String: Any]
        )
        let backupPath = try XCTUnwrap(recordObject["source_backup_path"] as? String)
        XCTAssertEqual(try Data(contentsOf: URL(fileURLWithPath: backupPath)), originalBytes)

        let restored = try await trash.restore(
            trashID: result.trashID,
            agent: detail.sourceAgent
        )
        XCTAssertEqual(restored.restoredID, detail.id)
        XCTAssertEqual(try Data(contentsOf: sourceURL), originalBytes)
        let recordsAfterRestore = try await trash.list()
        XCTAssertTrue(recordsAfterRestore.isEmpty)
        let restoredConversation = try await conversations.readConversation(
            agent: detail.sourceAgent,
            id: detail.id
        )
        XCTAssertEqual(restoredConversation.messages.first?.content, "preserve me")
    }

    func testPermanentTrashDeletionAlsoRemovesRawSourceBackup() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("NativeTrashPurgeTests-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: root) }
        let databaseURL = root.appendingPathComponent("app/aimemory.db")
        var database: NativeDatabase? = try NativeDatabase(url: databaseURL)
        _ = try await database?.currentSchemaVersion()
        database = nil
        let conversations = NativeConversationStore(databaseURL: databaseURL)
        let sourceURL = root.appendingPathComponent("gemini/session.json")
        try FileManager.default.createDirectory(
            at: sourceURL.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        try Data("source".utf8).write(to: sourceURL)
        let detail = conversation(
            id: "purge-file",
            agent: "gemini",
            storagePath: sourceURL.path
        )
        try await conversations.upsertConversation(detail)
        let trash = NativeTrashStore(
            root: root.appendingPathComponent("trash"),
            conversations: conversations,
            sourceWriter: NativeAgentConversationWriter(home: root)
        )
        let result = try await trash.trash(
            agent: detail.sourceAgent,
            id: detail.id,
            retentionDays: 14
        )
        let recordsAfterTrash = try await trash.list()
        let record = try XCTUnwrap(recordsAfterTrash.first)
        let data = try Data(contentsOf: URL(fileURLWithPath: record.recordPath))
        let object = try XCTUnwrap(
            JSONSerialization.jsonObject(with: data) as? [String: Any]
        )
        let backupPath = try XCTUnwrap(object["source_backup_path"] as? String)
        XCTAssertTrue(FileManager.default.fileExists(atPath: backupPath))

        try await trash.delete(
            trashID: result.trashID,
            agent: detail.sourceAgent
        )

        XCTAssertFalse(FileManager.default.fileExists(atPath: backupPath))
        let recordsAfterDelete = try await trash.list()
        XCTAssertTrue(recordsAfterDelete.isEmpty)
    }

    func testExpiredTrashPurgesRecordAndRawSourceBackup() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("NativeTrashExpiryTests-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: root) }
        let databaseURL = root.appendingPathComponent("app/aimemory.db")
        var database: NativeDatabase? = try NativeDatabase(url: databaseURL)
        _ = try await database?.currentSchemaVersion()
        database = nil
        let conversations = NativeConversationStore(databaseURL: databaseURL)
        let sourceURL = root.appendingPathComponent("zcode/task.json")
        try FileManager.default.createDirectory(
            at: sourceURL.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        try Data("source".utf8).write(to: sourceURL)
        let detail = conversation(
            id: "expires",
            agent: "zcode",
            storagePath: sourceURL.path
        )
        try await conversations.upsertConversation(detail)
        let clock = TrashTestClock(
            Date(timeIntervalSince1970: 1_750_000_000)
        )
        let trash = NativeTrashStore(
            root: root.appendingPathComponent("trash"),
            conversations: conversations,
            sourceWriter: NativeAgentConversationWriter(home: root),
            now: { clock.value }
        )
        _ = try await trash.trash(
            agent: detail.sourceAgent,
            id: detail.id,
            retentionDays: 1
        )
        let initialRecords = try await trash.list()
        let record = try XCTUnwrap(initialRecords.first)
        let data = try Data(contentsOf: URL(fileURLWithPath: record.recordPath))
        let object = try XCTUnwrap(
            JSONSerialization.jsonObject(with: data) as? [String: Any]
        )
        let backupPath = try XCTUnwrap(object["source_backup_path"] as? String)
        XCTAssertTrue(FileManager.default.fileExists(atPath: backupPath))

        clock.value = clock.value.addingTimeInterval(2 * 24 * 60 * 60)
        let expiredRecords = try await trash.list()

        XCTAssertTrue(expiredRecords.isEmpty)
        XCTAssertFalse(FileManager.default.fileExists(atPath: backupPath))
        XCTAssertFalse(FileManager.default.fileExists(atPath: record.recordPath))
    }

    func testOpenCodeTrashArchivesAndRestoresOriginalSession() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("NativeTrashOpenCodeTests-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: root) }
        let databaseURL = root.appendingPathComponent("app/aimemory.db")
        var database: NativeDatabase? = try NativeDatabase(url: databaseURL)
        _ = try await database?.currentSchemaVersion()
        database = nil
        let conversations = NativeConversationStore(databaseURL: databaseURL)
        let openCodeURL = root.appendingPathComponent(
            ".local/share/opencode/opencode.db"
        )
        try createOpenCodeFixture(at: openCodeURL, id: "ses_restore")
        let detail = conversation(
            id: "ses_restore",
            agent: "opencode",
            storagePath: openCodeURL.path
        )
        try await conversations.upsertConversation(detail)
        let trash = NativeTrashStore(
            root: root.appendingPathComponent("trash"),
            conversations: conversations,
            sourceWriter: NativeAgentConversationWriter(home: root)
        )

        let result = try await trash.trash(
            agent: detail.sourceAgent,
            id: detail.id,
            retentionDays: 14
        )
        XCTAssertTrue(try openCodeArchived(at: openCodeURL, id: detail.id))

        let restored = try await trash.restore(
            trashID: result.trashID,
            agent: detail.sourceAgent
        )

        XCTAssertEqual(restored.restoredID, detail.id)
        XCTAssertFalse(try openCodeArchived(at: openCodeURL, id: detail.id))
        let restoredConversation = try await conversations.readConversation(
            agent: detail.sourceAgent,
            id: detail.id
        )
        XCTAssertEqual(restoredConversation.messages.first?.content, "preserve me")
    }

    func testKimiTrashBacksUpAndRestoresCompleteSessionDirectory() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("NativeTrashKimiTests-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: root) }
        let databaseURL = root.appendingPathComponent("app/aimemory.db")
        var database: NativeDatabase? = try NativeDatabase(url: databaseURL)
        _ = try await database?.currentSchemaVersion()
        database = nil
        let conversations = NativeConversationStore(databaseURL: databaseURL)
        let sessionURL = root.appendingPathComponent(
            ".kimi/sessions/workspace/session-1"
        )
        let stateURL = sessionURL.appendingPathComponent("state.json")
        let wireURL = sessionURL.appendingPathComponent("agents/main/wire.jsonl")
        try FileManager.default.createDirectory(
            at: wireURL.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        try Data(#"{"title":"Kimi session"}"#.utf8).write(to: stateURL)
        try Data(#"{"type":"turn.prompt","content":"hello"}\n"#.utf8)
            .write(to: wireURL)
        let detail = conversation(
            id: "session-1",
            agent: "kimi",
            storagePath: stateURL.path
        )
        try await conversations.upsertConversation(detail)
        let trash = NativeTrashStore(
            root: root.appendingPathComponent("trash"),
            conversations: conversations,
            sourceWriter: NativeAgentConversationWriter(home: root)
        )

        let result = try await trash.trash(
            agent: detail.sourceAgent,
            id: detail.id,
            retentionDays: 14
        )
        XCTAssertFalse(FileManager.default.fileExists(atPath: sessionURL.path))

        _ = try await trash.restore(
            trashID: result.trashID,
            agent: detail.sourceAgent
        )

        XCTAssertTrue(FileManager.default.fileExists(atPath: stateURL.path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: wireURL.path))
        XCTAssertEqual(
            try String(contentsOf: wireURL, encoding: .utf8),
            #"{"type":"turn.prompt","content":"hello"}\n"#
        )
    }

    func testRestoreRefusesToOverwriteNewSourceAtOriginalPath() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("NativeTrashConflictTests-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: root) }
        let databaseURL = root.appendingPathComponent("app/aimemory.db")
        var database: NativeDatabase? = try NativeDatabase(url: databaseURL)
        _ = try await database?.currentSchemaVersion()
        database = nil
        let conversations = NativeConversationStore(databaseURL: databaseURL)
        let sourceURL = root.appendingPathComponent("antigravity/task.json")
        try FileManager.default.createDirectory(
            at: sourceURL.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        try Data("original".utf8).write(to: sourceURL)
        let detail = conversation(
            id: "restore-conflict",
            agent: "antigravity",
            storagePath: sourceURL.path
        )
        try await conversations.upsertConversation(detail)
        let trash = NativeTrashStore(
            root: root.appendingPathComponent("trash"),
            conversations: conversations,
            sourceWriter: NativeAgentConversationWriter(home: root)
        )
        let result = try await trash.trash(
            agent: detail.sourceAgent,
            id: detail.id,
            retentionDays: 14
        )
        try Data("new source".utf8).write(to: sourceURL)

        do {
            _ = try await trash.restore(
                trashID: result.trashID,
                agent: detail.sourceAgent
            )
            XCTFail("Expected restore conflict")
        } catch let error as NativeTrashError {
            guard case .sourceRestoreConflict(let path) = error else {
                return XCTFail("Unexpected error: \(error)")
            }
            XCTAssertEqual(path, sourceURL.path)
        }

        XCTAssertEqual(try Data(contentsOf: sourceURL), Data("new source".utf8))
        let recordsAfterConflict = try await trash.list()
        XCTAssertEqual(recordsAfterConflict.count, 1)
    }

    private func conversation(
        id: String,
        agent: String,
        storagePath: String
    ) -> ConversationDetail {
        ConversationDetail(
            id: id,
            sourceAgent: agent,
            projectDir: "/tmp/trash-project",
            createdAt: "2026-07-23T10:00:00Z",
            updatedAt: "2026-07-23T11:00:00Z",
            summary: "Trash source roundtrip",
            storagePath: storagePath,
            resumeCommand: nil,
            messages: [
                ConversationMessage(
                    id: "message-1",
                    timestamp: "2026-07-23T10:01:00Z",
                    role: "user",
                    content: "preserve me",
                    toolCalls: [],
                    metadata: [:]
                ),
            ],
            fileChanges: []
        )
    }

    private func createOpenCodeFixture(at url: URL, id: String) throws {
        try FileManager.default.createDirectory(
            at: url.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        var database: OpaquePointer?
        guard sqlite3_open(url.path, &database) == SQLITE_OK, let database else {
            throw NSError(domain: "NativeTrashStoreTests", code: 1)
        }
        defer { sqlite3_close(database) }
        let sql = """
        CREATE TABLE session (
          id TEXT PRIMARY KEY,
          time_archived INTEGER,
          time_updated INTEGER NOT NULL
        );
        INSERT INTO session (id, time_archived, time_updated)
        VALUES ('\(id)', NULL, 1);
        """
        guard sqlite3_exec(database, sql, nil, nil, nil) == SQLITE_OK else {
            throw NSError(
                domain: "NativeTrashStoreTests",
                code: 2,
                userInfo: [
                    NSLocalizedDescriptionKey: String(cString: sqlite3_errmsg(database))
                ]
            )
        }
    }

    private func openCodeArchived(at url: URL, id: String) throws -> Bool {
        var database: OpaquePointer?
        guard sqlite3_open_v2(
            url.path,
            &database,
            SQLITE_OPEN_READONLY,
            nil
        ) == SQLITE_OK, let database else {
            throw NSError(domain: "NativeTrashStoreTests", code: 3)
        }
        defer { sqlite3_close(database) }
        var statement: OpaquePointer?
        guard sqlite3_prepare_v2(
            database,
            "SELECT time_archived FROM session WHERE id = ?;",
            -1,
            &statement,
            nil
        ) == SQLITE_OK, let statement else {
            throw NSError(domain: "NativeTrashStoreTests", code: 4)
        }
        defer { sqlite3_finalize(statement) }
        sqlite3_bind_text(
            statement,
            1,
            id,
            -1,
            unsafeBitCast(-1, to: sqlite3_destructor_type.self)
        )
        guard sqlite3_step(statement) == SQLITE_ROW else {
            throw NSError(domain: "NativeTrashStoreTests", code: 5)
        }
        return sqlite3_column_type(statement, 0) != SQLITE_NULL
    }
}

private final class TrashTestClock: @unchecked Sendable {
    var value: Date

    init(_ value: Date) {
        self.value = value
    }
}
