import Foundation
import SQLite3

struct NativeImportResult: Sendable {
    let sourcePath: String
    let destinationPath: String
    let backupPath: String?
    let schemaVersion: Int

    var dictionary: [String: Any] {
        var result: [String: Any] = [
            "source_path": sourcePath,
            "destination_path": destinationPath,
            "schema_version": schemaVersion,
        ]
        if let backupPath { result["backup_path"] = backupPath }
        return result
    }
}

/// Transactional, source-read-only ChatMem database import.
enum NativeDatabaseImporter {
    static func importChatMem(
        source: URL,
        destination: URL = DataPaths.dbURL
    ) async throws -> NativeImportResult {
        let sourcePath = source.standardizedFileURL.path
        let destinationPath = destination.standardizedFileURL.path
        guard sourcePath != destinationPath else {
            throw NativeDatabaseImportError.sourceEqualsDestination
        }
        guard FileManager.default.isReadableFile(atPath: sourcePath) else {
            throw NativeDatabaseImportError.unreadableSource(sourcePath)
        }
        try FileManager.default.createDirectory(
            at: destination.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )

        try validate(url: source, check: "quick_check", readOnly: true)

        let backupURL: URL?
        if FileManager.default.fileExists(atPath: destinationPath) {
            backupURL = destination.deletingLastPathComponent().appendingPathComponent(
                "\(destination.lastPathComponent).pre-import-\(timestamp())"
            )
            try onlineBackup(source: destination, destination: backupURL!)
            try validate(url: backupURL!, check: "quick_check", readOnly: false)
        } else {
            backupURL = nil
        }

        let staging = destination.deletingLastPathComponent().appendingPathComponent(
            ".\(destination.lastPathComponent).import-\(UUID().uuidString)"
        )
        defer {
            try? FileManager.default.removeItem(at: staging)
            try? FileManager.default.removeItem(
                at: URL(fileURLWithPath: staging.path + "-wal")
            )
            try? FileManager.default.removeItem(
                at: URL(fileURLWithPath: staging.path + "-shm")
            )
        }

        do {
            try onlineBackup(source: source, destination: staging)
            var migrated: NativeDatabase? = try NativeDatabase(
                url: staging,
                createMigrationBackup: false
            )
            let version = try await migrated!.currentSchemaVersion()
            guard version == NativeDatabase.schemaVersion else {
                throw NativeDatabaseImportError.invalidMigratedVersion(version)
            }
            migrated = nil
            try validate(url: staging, check: "integrity_check", readOnly: false)

            let fileManager = FileManager.default
            if fileManager.fileExists(atPath: destinationPath) {
                _ = try fileManager.replaceItemAt(
                    destination,
                    withItemAt: staging,
                    backupItemName: nil,
                    options: []
                )
            } else {
                try fileManager.moveItem(at: staging, to: destination)
            }
            try? fileManager.removeItem(
                at: URL(fileURLWithPath: destination.path + "-wal")
            )
            try? fileManager.removeItem(
                at: URL(fileURLWithPath: destination.path + "-shm")
            )
            try validate(url: destination, check: "quick_check", readOnly: false)
            return NativeImportResult(
                sourcePath: sourcePath,
                destinationPath: destinationPath,
                backupPath: backupURL?.path,
                schemaVersion: version
            )
        } catch {
            throw NativeDatabaseImportError.importFailed(
                underlying: error,
                backupPath: backupURL?.path
            )
        }
    }

    private static func onlineBackup(source: URL, destination: URL) throws {
        var sourceDB: OpaquePointer?
        guard sqlite3_open_v2(
            source.path,
            &sourceDB,
            SQLITE_OPEN_READONLY | SQLITE_OPEN_FULLMUTEX,
            nil
        ) == SQLITE_OK, let sourceDB else {
            let message = sourceDB.map { String(cString: sqlite3_errmsg($0)) }
                ?? "unable to open source"
            if let sourceDB { sqlite3_close(sourceDB) }
            throw NativeDatabaseImportError.sqlite(message)
        }
        defer { sqlite3_close(sourceDB) }

        var destinationDB: OpaquePointer?
        guard sqlite3_open_v2(
            destination.path,
            &destinationDB,
            SQLITE_OPEN_CREATE | SQLITE_OPEN_READWRITE | SQLITE_OPEN_FULLMUTEX,
            nil
        ) == SQLITE_OK, let destinationDB else {
            let message = destinationDB.map { String(cString: sqlite3_errmsg($0)) }
                ?? "unable to create destination"
            if let destinationDB { sqlite3_close(destinationDB) }
            throw NativeDatabaseImportError.sqlite(message)
        }
        defer { sqlite3_close(destinationDB) }

        guard let backup = sqlite3_backup_init(destinationDB, "main", sourceDB, "main") else {
            throw NativeDatabaseImportError.sqlite(
                String(cString: sqlite3_errmsg(destinationDB))
            )
        }
        let step = sqlite3_backup_step(backup, -1)
        let finish = sqlite3_backup_finish(backup)
        guard step == SQLITE_DONE, finish == SQLITE_OK else {
            throw NativeDatabaseImportError.sqlite(
                String(cString: sqlite3_errmsg(destinationDB))
            )
        }
    }

    private static func validate(url: URL, check: String, readOnly: Bool) throws {
        var connection: OpaquePointer?
        guard sqlite3_open_v2(
            url.path,
            &connection,
            (readOnly ? SQLITE_OPEN_READONLY : SQLITE_OPEN_READWRITE)
                | SQLITE_OPEN_FULLMUTEX,
            nil
        ) == SQLITE_OK, let connection else {
            let message = connection.map { String(cString: sqlite3_errmsg($0)) }
                ?? "unable to validate database"
            if let connection { sqlite3_close(connection) }
            throw NativeDatabaseImportError.sqlite(message)
        }
        defer { sqlite3_close(connection) }
        var statement: OpaquePointer?
        guard sqlite3_prepare_v2(
            connection,
            "PRAGMA \(check);",
            -1,
            &statement,
            nil
        ) == SQLITE_OK, let statement else {
            throw NativeDatabaseImportError.sqlite(
                String(cString: sqlite3_errmsg(connection))
            )
        }
        defer { sqlite3_finalize(statement) }
        guard sqlite3_step(statement) == SQLITE_ROW,
              let value = sqlite3_column_text(statement, 0),
              String(cString: value).lowercased() == "ok"
        else {
            throw NativeDatabaseImportError.integrityCheckFailed(url.path)
        }
    }

    private static func timestamp() -> String {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.dateFormat = "yyyyMMdd-HHmmss"
        return formatter.string(from: Date())
    }
}

enum NativeDatabaseImportError: LocalizedError {
    case sourceEqualsDestination
    case unreadableSource(String)
    case integrityCheckFailed(String)
    case invalidMigratedVersion(Int)
    case sqlite(String)
    case importFailed(underlying: Error, backupPath: String?)

    var errorDescription: String? {
        switch self {
        case .sourceEqualsDestination:
            "导入源不能是 AI Memory 当前数据库。"
        case .unreadableSource(let path):
            "无法读取 ChatMem 数据库：\(path)"
        case .integrityCheckFailed(let path):
            "数据库完整性检查失败：\(path)"
        case .invalidMigratedVersion(let version):
            "导入后的数据库版本无效：\(version)"
        case .sqlite(let message):
            "SQLite 导入失败：\(message)"
        case .importFailed(let underlying, let backupPath):
            if let backupPath {
                "导入失败，原 AI Memory 数据已保留在 \(backupPath)：\(underlying.localizedDescription)"
            } else {
                "导入失败：\(underlying.localizedDescription)"
            }
        }
    }
}
