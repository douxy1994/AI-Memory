// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
import XCTest
import SQLite3
@testable import AIMemory

final class NativeDatabaseImporterTests: XCTestCase {
    func testImportBacksUpDestinationAndAtomicallyReplacesIt() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("NativeDatabaseImporterTests-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: root) }
        let source = root.appendingPathComponent("chatmem.db")
        let destination = root.appendingPathComponent("aimemory.db")
        try await makeDatabase(at: source, marker: "source")
        try await makeDatabase(at: destination, marker: "destination")

        let result = try await NativeDatabaseImporter.importChatMem(
            source: source,
            destination: destination
        )

        XCTAssertEqual(result.schemaVersion, NativeDatabase.schemaVersion)
        XCTAssertEqual(try marker(at: source), "source")
        XCTAssertEqual(try marker(at: destination), "source")
        let backupPath = try XCTUnwrap(result.backupPath)
        XCTAssertEqual(try marker(at: URL(fileURLWithPath: backupPath)), "destination")
    }

    func testInvalidSourceDoesNotReplaceDestination() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("NativeDatabaseImporterTests-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let source = root.appendingPathComponent("invalid.db")
        let destination = root.appendingPathComponent("aimemory.db")
        try Data("not a sqlite database".utf8).write(to: source)
        try await makeDatabase(at: destination, marker: "destination")

        do {
            _ = try await NativeDatabaseImporter.importChatMem(
                source: source,
                destination: destination
            )
            XCTFail("Invalid input must fail")
        } catch {
            XCTAssertEqual(try marker(at: destination), "destination")
        }
    }

    private func makeDatabase(at url: URL, marker: String) async throws {
        var database: NativeDatabase? = try NativeDatabase(url: url)
        _ = try await database?.currentSchemaVersion()
        database = nil
        var raw: OpaquePointer?
        guard sqlite3_open(url.path, &raw) == SQLITE_OK, let raw else {
            throw TestError.sqlite
        }
        defer { sqlite3_close(raw) }
        let sql = """
        CREATE TABLE import_marker(value TEXT NOT NULL);
        INSERT INTO import_marker VALUES('\(marker)');
        """
        guard sqlite3_exec(raw, sql, nil, nil, nil) == SQLITE_OK else {
            throw TestError.sqlite
        }
    }

    private func marker(at url: URL) throws -> String {
        var raw: OpaquePointer?
        guard sqlite3_open_v2(
            url.path,
            &raw,
            SQLITE_OPEN_READWRITE | SQLITE_OPEN_FULLMUTEX,
            nil
        ) == SQLITE_OK, let raw else {
            throw TestError.sqlite
        }
        defer { sqlite3_close(raw) }
        var statement: OpaquePointer?
        guard sqlite3_prepare_v2(
            raw,
            "SELECT value FROM import_marker;",
            -1,
            &statement,
            nil
        ) == SQLITE_OK, let statement else {
            throw TestError.sqlite
        }
        defer { sqlite3_finalize(statement) }
        guard sqlite3_step(statement) == SQLITE_ROW,
              let value = sqlite3_column_text(statement, 0)
        else { throw TestError.sqlite }
        return String(cString: value)
    }

    private enum TestError: Error {
        case sqlite
    }
}
