// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
import XCTest
import SQLite3
@testable import AIMemory

final class NativeDatabaseTests: XCTestCase {
    func testFreshDatabaseHasVersionedCompatibleSchema() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("AIMemoryDatabaseTests-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }

        let database = try NativeDatabase(url: root.appendingPathComponent("aimemory.db"))
        let version = try await database.currentSchemaVersion()
        let tables = try await database.tableNames()

        XCTAssertEqual(version, NativeDatabase.schemaVersion)
        XCTAssertTrue(tables.contains("schema_migrations"))
        XCTAssertTrue(tables.contains("conversations"))
        XCTAssertTrue(tables.contains("memory_candidates"))
        XCTAssertTrue(tables.contains("approved_memories"))
        XCTAssertTrue(tables.contains("handoff_packets"))
        XCTAssertTrue(tables.contains("checkpoints"))
        XCTAssertTrue(tables.contains("agent_runs"))
        XCTAssertTrue(tables.contains("artifacts"))
        XCTAssertTrue(tables.contains("search_documents_fts"))
    }

    func testMigrationIsIdempotent() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("AIMemoryDatabaseTests-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let url = root.appendingPathComponent("aimemory.db")

        var database: NativeDatabase? = try NativeDatabase(url: url)
        let firstVersion = try await database?.currentSchemaVersion()
        XCTAssertEqual(firstVersion, NativeDatabase.schemaVersion)
        database = nil

        let reopened = try NativeDatabase(url: url)
        let reopenedVersion = try await reopened.currentSchemaVersion()
        XCTAssertEqual(reopenedVersion, NativeDatabase.schemaVersion)
    }

    func testLegacyDatabaseIsBackedUpBeforeMigration() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("AIMemoryDatabaseTests-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let url = root.appendingPathComponent("aimemory.db")

        var raw: OpaquePointer?
        XCTAssertEqual(sqlite3_open(url.path, &raw), SQLITE_OK)
        XCTAssertEqual(
            sqlite3_exec(
                raw,
                "CREATE TABLE legacy_marker(value TEXT); INSERT INTO legacy_marker VALUES('keep');",
                nil,
                nil,
                nil
            ),
            SQLITE_OK
        )
        sqlite3_close(raw)

        let database = try NativeDatabase(url: url)
        let version = try await database.currentSchemaVersion()
        XCTAssertEqual(version, NativeDatabase.schemaVersion)
        let backups = try FileManager.default.contentsOfDirectory(
            at: root,
            includingPropertiesForKeys: nil
        ).filter { $0.lastPathComponent.hasPrefix("aimemory.db.backup-v0-") }
        XCTAssertEqual(backups.count, 1)

        var backup: OpaquePointer?
        let openResult = sqlite3_open_v2(
            backups[0].path,
            &backup,
            // SQLite backups retain the source database's WAL journal mode.
            // Opening that file read-only cannot create its transient -shm
            // sidecar and makes the first query fail with SQLITE_CANTOPEN.
            SQLITE_OPEN_READWRITE | SQLITE_OPEN_FULLMUTEX,
            nil
        )
        guard openResult == SQLITE_OK, let backup else {
            XCTFail("Unable to open migration backup: SQLite error \(openResult)")
            return
        }
        var statement: OpaquePointer?
        let prepareResult = sqlite3_prepare_v2(
            backup,
            "SELECT value FROM legacy_marker;",
            -1,
            &statement,
            nil
        )
        guard prepareResult == SQLITE_OK, let statement else {
            XCTFail("Unable to read migration backup: \(String(cString: sqlite3_errmsg(backup)))")
            sqlite3_close(backup)
            return
        }
        let stepResult = sqlite3_step(statement)
        guard stepResult == SQLITE_ROW, let value = sqlite3_column_text(statement, 0) else {
            XCTFail("The migration backup lost the legacy marker value")
            sqlite3_finalize(statement)
            sqlite3_close(backup)
            return
        }
        XCTAssertEqual(String(cString: value), "keep")
        sqlite3_finalize(statement)
        sqlite3_close(backup)
    }
}
