import XCTest
@testable import AIMemory

final class NativeSettingsStoreTests: XCTestCase {
    func testLegacySnakeCaseSettingsNormalizeToVersionedCanonicalForm() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("AIMemorySettingsTests-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let url = root.appendingPathComponent("settings.json")
        let legacy = """
        {
          "locale": "en",
          "font_family": "source-serif",
          "trash_retention_days": 500,
          "sync": {
            "webdav_host": "example.invalid",
            "sync_folder": "/tmp/sync"
          },
          "auto_backup_interval_minutes": 2
        }
        """
        try Data(legacy.utf8).write(to: url)

        let store = NativeSettingsStore(url: url)
        let settings = try await store.load()
        XCTAssertEqual(settings.schemaVersion, 1)
        XCTAssertEqual(settings.fontFamily, "source-serif")
        XCTAssertEqual(settings.trashRetentionDays, 365)
        XCTAssertEqual(settings.sync.webdavHost, "example.invalid")
        XCTAssertEqual(settings.sync.syncFolder, "/tmp/sync")
        XCTAssertEqual(settings.autoBackupIntervalMinutes, 5)

        try await store.save(settings)
        let saved = String(decoding: try Data(contentsOf: url), as: UTF8.self)
        XCTAssertTrue(saved.contains("\"schemaVersion\" : 1"))
        XCTAssertTrue(saved.contains("\"fontFamily\" : \"source-serif\""))
        XCTAssertFalse(saved.contains("font_family"))
    }

    func testSettingsSaveIsIdempotent() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("AIMemorySettingsTests-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let url = root.appendingPathComponent("settings.json")
        let store = NativeSettingsStore(url: url)

        var settings = AppPreferences()
        settings.locale = "en"
        settings.trashRetentionDays = 30
        settings.updateFeedURL = "https://example.invalid/releases/latest"
        settings.machineGroupNames = ["macos": "工作室 Mac"]
        settings.machineGroupOverrides = ["/Volumes/Archive/project": "macos"]
        try await store.save(settings)
        let first = try Data(contentsOf: url)
        let reloaded = try await store.load()
        XCTAssertEqual(reloaded.updateFeedURL, settings.updateFeedURL)
        XCTAssertEqual(reloaded.machineGroupNames, settings.machineGroupNames)
        XCTAssertEqual(reloaded.machineGroupOverrides, settings.machineGroupOverrides)
        try await store.save(reloaded)
        let second = try Data(contentsOf: url)
        XCTAssertEqual(first, second)
    }

    func testWindowsSettingsKeysLoadAndSaveAsMacCanonicalForm() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("AIMemoryWindowsSettingsTests-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let url = root.appendingPathComponent("settings.json")
        let windows = """
        {
          "settingsVersion": 1,
          "language": "en",
          "fontFamily": "sourceSerif",
          "autoCaptureMemory": false,
          "trashRetentionDays": 21,
          "sync": {
            "webdavScheme": "https",
            "webdavHost": "dav.example.test",
            "username": "alvis"
          }
        }
        """
        try Data(windows.utf8).write(to: url)

        let store = NativeSettingsStore(url: url)
        let settings = try await store.load()

        XCTAssertEqual(settings.schemaVersion, 1)
        XCTAssertEqual(settings.locale, "en")
        XCTAssertEqual(settings.fontFamily, "source-serif")
        XCTAssertFalse(settings.autoCaptureMemory)
        XCTAssertEqual(settings.trashRetentionDays, 21)
        XCTAssertEqual(settings.sync.webdavHost, "dav.example.test")
        XCTAssertEqual(settings.sync.username, "alvis")

        try await store.save(settings)
        let saved = String(decoding: try Data(contentsOf: url), as: UTF8.self)
        XCTAssertTrue(saved.contains("\"schemaVersion\" : 1"))
        XCTAssertTrue(saved.contains("\"locale\" : \"en\""))
        XCTAssertTrue(saved.contains("\"fontFamily\" : \"source-serif\""))
        XCTAssertFalse(saved.contains("settingsVersion"))
        XCTAssertFalse(saved.contains("\"language\""))
    }
}
