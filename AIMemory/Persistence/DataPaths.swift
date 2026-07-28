import Foundation

/// All AI Memory on-disk paths are independent from ChatMem. Use these
/// constants so no code accidentally reads/writes ChatMem's data.
enum DataPaths {
    /// `~/Library/Application Support/AIMemory/`
    static var supportDir: URL {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? FileManager.default.homeDirectoryForCurrentUser
        return base.appendingPathComponent("AIMemory", isDirectory: true)
    }

    static var dbURL: URL { supportDir.appendingPathComponent("aimemory.db") }
    static var settingsURL: URL { supportDir.appendingPathComponent("settings.json") }
    static var trashDir: URL { supportDir.appendingPathComponent("trash", isDirectory: true) }

    /// `~/Library/Caches/com.aimemory.app/`
    static var cacheDir: URL {
        let base = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask).first
            ?? FileManager.default.homeDirectoryForCurrentUser
        return base.appendingPathComponent("com.aimemory.app", isDirectory: true)
    }

    /// Keychain service for AI Memory (independent from ChatMem).
    static let keychainService = "com.aimemory.app.webdav"

    /// Read-only source service used by the one-time ChatMem WebDAV import.
    static let chatMemKeychainService = "com.chatmem.app.webdav"

    /// os_log subsystem.
    static let subsystem = "com.aimemory.app"

    /// Best-effort guess at the ChatMem DB for the import panel's default URL.
    static var chatMemDBURL: URL? {
        let fm = FileManager.default
        if let support = fm.urls(for: .applicationSupportDirectory, in: .userDomainMask).first {
            let db = support.appendingPathComponent("ChatMem/chatmem.db")
            if fm.fileExists(atPath: db.path) { return db }
        }
        return nil
    }

    /// Existing ChatMem settings are only ever read for an idempotent import.
    static var chatMemSettingsURL: URL? {
        let fm = FileManager.default
        guard let support = fm.urls(
            for: .applicationSupportDirectory,
            in: .userDomainMask
        ).first else { return nil }
        let settings = support.appendingPathComponent("ChatMem/settings.json")
        return fm.fileExists(atPath: settings.path) ? settings : nil
    }
}
