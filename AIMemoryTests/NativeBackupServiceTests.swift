import XCTest
import SQLite3
@testable import AIMemory

final class NativeBackupServiceTests: XCTestCase {
    func testRecoveryPointContainsConsistentDatabaseSettingsAndManifest() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("NativeBackupServiceTests-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: root) }
        let databaseURL = root.appendingPathComponent("aimemory.db")
        let settingsURL = root.appendingPathComponent("settings.json")
        let backupsURL = root.appendingPathComponent("backups")
        var database: NativeDatabase? = try NativeDatabase(url: databaseURL)
        _ = try await database?.currentSchemaVersion()
        database = nil
        try Data(#"{"schemaVersion":1}"#.utf8).write(to: settingsURL)

        let service = NativeBackupService(
            databaseURL: databaseURL,
            settingsURL: settingsURL,
            root: backupsURL
        )
        let first = try await service.createRecoveryPoint(reason: "unit-test")
        let snapshot = first.url
        XCTAssertTrue(first.created)

        XCTAssertTrue(
            FileManager.default.fileExists(
                atPath: snapshot.appendingPathComponent("aimemory.db").path
            )
        )
        XCTAssertTrue(
            FileManager.default.fileExists(
                atPath: snapshot.appendingPathComponent("settings.json").path
            )
        )
        let manifest = try JSONSerialization.jsonObject(
            with: Data(contentsOf: snapshot.appendingPathComponent("manifest.json"))
        ) as? [String: Any]
        XCTAssertEqual(manifest?["schema_version"] as? Int, 2)
        XCTAssertEqual(manifest?["storage_mode"] as? String, "incremental-hardlink")

        var raw: OpaquePointer?
        let backupDB = snapshot.appendingPathComponent("aimemory.db")
        XCTAssertEqual(sqlite3_open(backupDB.path, &raw), SQLITE_OK)
        var statement: OpaquePointer?
        XCTAssertEqual(sqlite3_prepare_v2(raw, "PRAGMA quick_check;", -1, &statement, nil), SQLITE_OK)
        XCTAssertEqual(sqlite3_step(statement), SQLITE_ROW)
        XCTAssertEqual(String(cString: sqlite3_column_text(statement, 0)), "ok")
        sqlite3_finalize(statement)
        sqlite3_close(raw)

        let unchanged = try await service.createRecoveryPoint(reason: "automatic")
        XCTAssertFalse(unchanged.created)
        XCTAssertEqual(
            unchanged.url.resolvingSymlinksInPath(),
            snapshot.resolvingSymlinksInPath()
        )
        XCTAssertEqual(
            try FileManager.default.contentsOfDirectory(
                at: backupsURL,
                includingPropertiesForKeys: [.isDirectoryKey]
            ).filter {
                (try? $0.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) == true
            }.count,
            1
        )

        try Data(#"{"schemaVersion":1,"locale":"en"}"#.utf8).write(
            to: settingsURL,
            options: [.atomic]
        )
        let changed = try await service.createRecoveryPoint(reason: "automatic")
        XCTAssertTrue(changed.created)
        XCTAssertFalse(changed.databaseChanged)
        XCTAssertTrue(changed.settingsChanged)
    }
}
