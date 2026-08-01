// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
import Foundation
import SQLite3
import CryptoKit

actor NativeBackupService {
    private let databaseURL: URL
    private let settingsURL: URL
    private let root: URL

    init(
        databaseURL: URL = DataPaths.dbURL,
        settingsURL: URL = DataPaths.settingsURL,
        root: URL = DataPaths.supportDir.appendingPathComponent("backups", isDirectory: true)
    ) {
        self.databaseURL = databaseURL
        self.settingsURL = settingsURL
        self.root = root
    }

    func createRecoveryPoint(
        reason: String,
        keep: Int = 10
    ) throws -> NativeBackupResult {
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let previous = try latestSnapshot()
        let folder = root.appendingPathComponent(
            "\(timestamp())-\(Self.safeComponent(reason))",
            isDirectory: true
        )
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        do {
            let databaseSnapshot = folder.appendingPathComponent("aimemory.db")
            let settingsSnapshot = folder.appendingPathComponent("settings.json")
            try backupDatabase(to: databaseSnapshot)
            if FileManager.default.fileExists(atPath: settingsURL.path) {
                try FileManager.default.copyItem(
                    at: settingsURL,
                    to: settingsSnapshot
                )
            }
            let databaseHash = try contentHash(databaseSnapshot)
            let settingsHash = try optionalContentHash(settingsSnapshot)
            let previousHashes = try previous.map(snapshotHashes)
            let databaseChanged = previousHashes?.database != databaseHash
            let settingsChanged = previousHashes?.settings != settingsHash

            if let previous, !databaseChanged, !settingsChanged {
                try FileManager.default.removeItem(at: folder)
                return NativeBackupResult(
                    url: previous,
                    created: false,
                    databaseChanged: false,
                    settingsChanged: false
                )
            }

            if let previous, !databaseChanged {
                try replaceWithHardLink(
                    source: previous.appendingPathComponent("aimemory.db"),
                    destination: databaseSnapshot
                )
            }
            if let previous, !settingsChanged,
               FileManager.default.fileExists(atPath: settingsSnapshot.path) {
                try replaceWithHardLink(
                    source: previous.appendingPathComponent("settings.json"),
                    destination: settingsSnapshot
                )
            }
            let manifest: [String: Any] = [
                "schema_version": 2,
                "created_at": ISO8601DateFormatter().string(from: Date()),
                "reason": reason,
                "source_database": databaseURL.path,
                "database_sha256": databaseHash,
                "settings_sha256": settingsHash ?? NSNull(),
                "database_changed": databaseChanged,
                "settings_changed": settingsChanged,
                "storage_mode": "incremental-hardlink",
            ]
            let data = try JSONSerialization.data(
                withJSONObject: manifest,
                options: [.prettyPrinted, .sortedKeys]
            )
            try data.write(
                to: folder.appendingPathComponent("manifest.json"),
                options: [.atomic]
            )
            try prune(keeping: max(1, keep))
            return NativeBackupResult(
                url: folder,
                created: true,
                databaseChanged: databaseChanged,
                settingsChanged: settingsChanged
            )
        } catch {
            try? FileManager.default.removeItem(at: folder)
            throw error
        }
    }

    private func latestSnapshot() throws -> URL? {
        try FileManager.default.contentsOfDirectory(
            at: root,
            includingPropertiesForKeys: [.isDirectoryKey],
            options: [.skipsHiddenFiles]
        )
        .filter {
            (try? $0.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) == true
        }
        .max { $0.lastPathComponent < $1.lastPathComponent }
    }

    private func snapshotHashes(
        _ snapshot: URL
    ) throws -> (database: String, settings: String?) {
        let manifestURL = snapshot.appendingPathComponent("manifest.json")
        if let data = try? Data(contentsOf: manifestURL),
           let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
           let database = object["database_sha256"] as? String {
            return (database, object["settings_sha256"] as? String)
        }
        return (
            try contentHash(snapshot.appendingPathComponent("aimemory.db")),
            try optionalContentHash(snapshot.appendingPathComponent("settings.json"))
        )
    }

    private func contentHash(_ url: URL) throws -> String {
        SHA256.hash(data: try Data(contentsOf: url))
            .map { String(format: "%02x", $0) }
            .joined()
    }

    private func optionalContentHash(_ url: URL) throws -> String? {
        guard FileManager.default.fileExists(atPath: url.path) else { return nil }
        return try contentHash(url)
    }

    private func replaceWithHardLink(source: URL, destination: URL) throws {
        guard FileManager.default.fileExists(atPath: source.path) else { return }
        if FileManager.default.fileExists(atPath: destination.path) {
            try FileManager.default.removeItem(at: destination)
        }
        do {
            try FileManager.default.linkItem(at: source, to: destination)
        } catch {
            try FileManager.default.copyItem(at: source, to: destination)
        }
    }

    private func backupDatabase(to destinationURL: URL) throws {
        guard FileManager.default.fileExists(atPath: databaseURL.path) else {
            throw NativeBackupError.databaseMissing
        }
        var source: OpaquePointer?
        guard sqlite3_open_v2(
            databaseURL.path,
            &source,
            SQLITE_OPEN_READONLY | SQLITE_OPEN_FULLMUTEX,
            nil
        ) == SQLITE_OK, let source else {
            let message = source.map { String(cString: sqlite3_errmsg($0)) }
                ?? "unable to open source"
            if let source { sqlite3_close(source) }
            throw NativeBackupError.sqlite(message)
        }
        defer { sqlite3_close(source) }

        var destination: OpaquePointer?
        guard sqlite3_open_v2(
            destinationURL.path,
            &destination,
            SQLITE_OPEN_CREATE | SQLITE_OPEN_READWRITE | SQLITE_OPEN_FULLMUTEX,
            nil
        ) == SQLITE_OK, let destination else {
            let message = destination.map { String(cString: sqlite3_errmsg($0)) }
                ?? "unable to open destination"
            if let destination { sqlite3_close(destination) }
            throw NativeBackupError.sqlite(message)
        }
        defer { sqlite3_close(destination) }
        guard let backup = sqlite3_backup_init(destination, "main", source, "main") else {
            throw NativeBackupError.sqlite(String(cString: sqlite3_errmsg(destination)))
        }
        let step = sqlite3_backup_step(backup, -1)
        let finish = sqlite3_backup_finish(backup)
        guard step == SQLITE_DONE, finish == SQLITE_OK else {
            throw NativeBackupError.sqlite(String(cString: sqlite3_errmsg(destination)))
        }
    }

    private func prune(keeping count: Int) throws {
        let urls = try FileManager.default.contentsOfDirectory(
            at: root,
            includingPropertiesForKeys: [.creationDateKey, .isDirectoryKey],
            options: [.skipsHiddenFiles]
        ).filter {
            (try? $0.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) == true
        }.sorted {
            $0.lastPathComponent > $1.lastPathComponent
        }
        for expired in urls.dropFirst(count) {
            try FileManager.default.removeItem(at: expired)
        }
    }

    private func timestamp() -> String {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.dateFormat = "yyyyMMdd-HHmmss-SSS"
        return formatter.string(from: Date())
    }

    private static func safeComponent(_ value: String) -> String {
        let allowed = CharacterSet.alphanumerics.union(CharacterSet(charactersIn: "-_"))
        let cleaned = value.unicodeScalars.map { allowed.contains($0) ? Character(String($0)) : "-" }
        return String(cleaned).prefix(40).description
    }
}

struct NativeBackupResult: Sendable {
    let url: URL
    let created: Bool
    let databaseChanged: Bool
    let settingsChanged: Bool
}

enum NativeBackupError: LocalizedError {
    case databaseMissing
    case sqlite(String)

    var errorDescription: String? {
        switch self {
        case .databaseMissing:
            "AI Memory 数据库不存在。"
        case .sqlite(let message):
            "创建恢复点失败：\(message)"
        }
    }
}
