// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
import Foundation
import CryptoKit

/// Incremental file sync for a user-selected cloud-backed folder.
///
/// The shared on-disk layout is intentionally independent from WebDAV:
///
/// ```text
/// <selected folder>/
///   conversations/<agent>/<base64url(utf8 conversation id)>.json
///   manifest.json                         # status only; never an index
/// ```
///
/// Older Windows builds wrote the same conversation files below
/// `<selected folder>/AIMemorySync/`. That layout remains read-only compatible;
/// new writes always use the selected folder directly. Conversations are found
/// by scanning validated JSON payloads rather than trusting either manifest.
actor NativeLocalSyncService {
    private static let layoutSchemaVersion = 3
    private static let canonicalLayout = "aimemory-local-folder-v1"
    private static let canonicalConversationsFolder = "conversations"
    private static let legacyWindowsFolder = "AIMemorySync"
    private static let manifestFilename = "manifest.json"

    private let store: NativeConversationStore
    private let encoder = JSONEncoder()
    private let decoder = JSONDecoder()

    init(store: NativeConversationStore = NativeConversationStore()) {
        self.store = store
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
    }

    func status(folder: String) throws -> NativeLocalSyncStatus {
        let root = try Self.rootURL(for: folder)
        var isDirectory: ObjCBool = false
        let exists = FileManager.default.fileExists(
            atPath: root.path,
            isDirectory: &isDirectory
        ) && isDirectory.boolValue
        guard exists else {
            return NativeLocalSyncStatus(
                available: false,
                folderPath: root.path,
                remoteConversationCount: 0,
                lastSyncInfo: nil
            )
        }

        let remote = try readRemote(folder: root)
        let manifest = try Self.childURL(
            Self.manifestFilename,
            under: root
        )
        var lastSync: String?
        if let data = try? Data(contentsOf: manifest),
           let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any] {
            lastSync = object["last_synced_at"] as? String
        }
        return NativeLocalSyncStatus(
            available: true,
            folderPath: root.path,
            remoteConversationCount: remote.count,
            lastSyncInfo: lastSync
        )
    }

    func readiness(folder: String) -> NativeCloudReadiness {
        guard let root = try? Self.rootURL(for: folder) else {
            return NativeCloudReadiness(
                folderExists: false,
                isQuiet: true,
                hasLockFiles: false,
                recommendedAction: "folder_missing"
            )
        }
        var isDirectory: ObjCBool = false
        guard FileManager.default.fileExists(atPath: root.path, isDirectory: &isDirectory),
              isDirectory.boolValue else {
            return NativeCloudReadiness(
                folderExists: false,
                isQuiet: true,
                hasLockFiles: false,
                recommendedAction: "folder_missing"
            )
        }
        let hasLocks = hasActiveLockFiles(in: root)
        let recentlyModified: Bool
        if let values = try? root.resourceValues(forKeys: [.contentModificationDateKey]),
           let modified = values.contentModificationDate {
            recentlyModified = Date().timeIntervalSince(modified) < 3
        } else {
            recentlyModified = false
        }
        let quiet = !hasLocks && !recentlyModified
        return NativeCloudReadiness(
            folderExists: true,
            isQuiet: quiet,
            hasLockFiles: hasLocks,
            recommendedAction: quiet ? "safe_to_sync" : "wait"
        )
    }

    func sync(folder: String) async throws -> NativeLocalSyncResult {
        let root = try Self.rootURL(for: folder)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let conversationsRoot = try Self.childURL(
            Self.canonicalConversationsFolder,
            under: root
        )
        try FileManager.default.createDirectory(
            at: conversationsRoot,
            withIntermediateDirectories: true
        )

        var local: [SyncKey: LocalPayload] = [:]
        for conversation in try await store.exportAllConversationsForSync() {
            guard let key = SyncKey(
                agent: conversation.sourceAgent,
                id: conversation.id
            ) else {
                throw Self.invalidConversationError(conversation)
            }
            local[key] = LocalPayload(
                detail: conversation,
                hash: Self.semanticHash(conversation)
            )
        }

        let remote = try readRemote(folder: root)
        let allKeys = Set(local.keys).union(remote.keys)
        var uploaded = 0
        var downloaded = 0
        var skipped = 0
        var conflicts = 0

        for key in allKeys.sorted(by: {
            ($0.agent, $0.id) < ($1.agent, $1.id)
        }) {
            switch (local[key], remote[key]) {
            case (.some(let localConversation), .none):
                try writeRemote(localConversation.detail, key: key, root: conversationsRoot)
                uploaded += 1
            case (.none, .some(let remoteConversation)):
                try await store.upsertConversation(remoteConversation.detail)
                downloaded += 1
            case (.some(let localConversation), .some(let remoteConversation)):
                if localConversation.hash == remoteConversation.hash {
                    skipped += 1
                } else if Self.isNewer(
                    localConversation.detail.updatedAt,
                    than: remoteConversation.detail.updatedAt
                ) {
                    try writeRemote(localConversation.detail, key: key, root: conversationsRoot)
                    uploaded += 1
                    conflicts += 1
                } else if Self.isNewer(
                    remoteConversation.detail.updatedAt,
                    than: localConversation.detail.updatedAt
                ) {
                    try await store.upsertConversation(remoteConversation.detail)
                    downloaded += 1
                    conflicts += 1
                } else {
                    // Equal timestamps with different logical content keep the
                    // local copy, matching the pre-existing conflict policy.
                    // The semantic hash intentionally ignores serializer-only
                    // differences (metadata, resume command, null vs empty).
                    try writeRemote(localConversation.detail, key: key, root: conversationsRoot)
                    uploaded += 1
                    conflicts += 1
                }
            case (.none, .none):
                break
            }
        }

        try writeStatusManifest(
            root: root,
            uploaded: uploaded,
            downloaded: downloaded,
            skipped: skipped,
            conflicts: conflicts,
            totalLocal: local.count,
            totalRemote: remote.count
        )
        return NativeLocalSyncResult(
            uploaded: uploaded,
            downloaded: downloaded,
            skipped: skipped,
            conflictsResolved: conflicts,
            folderPath: root.path
        )
    }

    /// Scans canonical files plus the read-only legacy Windows subfolder. A
    /// manifest never contributes a conversation to this result.
    private func readRemote(folder: URL) throws -> [SyncKey: RemotePayload] {
        var result: [SyncKey: RemotePayload] = [:]
        let canonicalRoot = try Self.childURL(
            Self.canonicalConversationsFolder,
            under: folder
        )
        scanRemote(
            conversationsRoot: canonicalRoot,
            layoutPriority: 2,
            into: &result
        )

        let legacyRoot = try Self.childURL(
            [Self.legacyWindowsFolder, Self.canonicalConversationsFolder],
            under: folder
        )
        scanRemote(
            conversationsRoot: legacyRoot,
            layoutPriority: 0,
            into: &result
        )
        return result
    }

    private func scanRemote(
        conversationsRoot: URL,
        layoutPriority: Int,
        into result: inout [SyncKey: RemotePayload]
    ) {
        guard Self.isDirectory(conversationsRoot),
              !Self.isSymbolicLink(conversationsRoot),
              let agentFolders = try? FileManager.default.contentsOfDirectory(
                at: conversationsRoot,
                includingPropertiesForKeys: [.isDirectoryKey],
                options: [.skipsHiddenFiles]
              )
        else { return }

        for agentFolder in agentFolders.sorted(by: { $0.path < $1.path }) {
            guard Self.isDirectory(agentFolder),
                  !Self.isSymbolicLink(agentFolder),
                  let directoryAgent = Self.normalizedAgent(agentFolder.lastPathComponent),
                  let files = try? FileManager.default.contentsOfDirectory(
                    at: agentFolder,
                    includingPropertiesForKeys: [.isRegularFileKey],
                    options: [.skipsHiddenFiles]
                  )
            else { continue }

            for file in files.sorted(by: { $0.path < $1.path }) {
                guard file.pathExtension.lowercased() == "json",
                      Self.isRegularFile(file),
                      !Self.isSymbolicLink(file),
                      let data = try? Data(contentsOf: file),
                      let conversation = try? decoder.decode(ConversationDetail.self, from: data),
                      let key = SyncKey(
                        agent: conversation.sourceAgent,
                        id: conversation.id
                      ),
                      key.agent == directoryAgent
                else { continue }

                let canonicalName = Self.canonicalFilename(key.id) + ".json"
                let priority = layoutPriority
                    + (file.lastPathComponent == canonicalName ? 1 : 0)
                let candidate = RemotePayload(
                    detail: conversation,
                    hash: Self.semanticHash(conversation),
                    priority: priority,
                    stablePath: file.path
                )
                if let existing = result[key] {
                    if Self.shouldReplace(existing: existing, with: candidate) {
                        result[key] = candidate
                    }
                } else {
                    result[key] = candidate
                }
            }
        }
    }

    private func writeRemote(
        _ conversation: ConversationDetail,
        key: SyncKey,
        root: URL
    ) throws {
        let agentRoot = try Self.childURL(key.agent, under: root)
        try FileManager.default.createDirectory(
            at: agentRoot,
            withIntermediateDirectories: true
        )
        let destination = try Self.childURL(
            Self.canonicalFilename(key.id) + ".json",
            under: agentRoot
        )
        let data = try encoder.encode(conversation)
        try data.write(to: destination, options: [.atomic])
    }

    private func writeStatusManifest(
        root: URL,
        uploaded: Int,
        downloaded: Int,
        skipped: Int,
        conflicts: Int,
        totalLocal: Int,
        totalRemote: Int
    ) throws {
        let manifest: [String: Any] = [
            "schema_version": Self.layoutSchemaVersion,
            "layout": Self.canonicalLayout,
            "last_synced_at": ISO8601DateFormatter().string(from: Date()),
            "sync_direction": "bidirectional",
            "uploaded": uploaded,
            "downloaded": downloaded,
            "skipped": skipped,
            "conflicts_resolved": conflicts,
            "total_local": totalLocal,
            "total_remote": totalRemote,
        ]
        let data = try JSONSerialization.data(
            withJSONObject: manifest,
            options: [.prettyPrinted, .sortedKeys]
        )
        let destination = try Self.childURL(Self.manifestFilename, under: root)
        try data.write(to: destination, options: [.atomic])
    }

    /// Shared semantic fingerprint. It covers every field that both local
    /// databases persist and deliberately excludes presentation/derived data:
    /// `resume_command` and message `metadata`. That prevents macOS and
    /// Windows serializers from rewriting the same conversation forever.
    nonisolated static func semanticHash(_ conversation: ConversationDetail) -> String {
        var data = Data()
        appendASCII("aimemory-local-sync-semantic-v1", to: &data)
        appendString(conversation.id, to: &data)
        appendString(normalizedAgent(conversation.sourceAgent) ?? conversation.sourceAgent, to: &data)
        appendString(conversation.projectDir, to: &data)
        appendString(conversation.createdAt, to: &data)
        appendString(conversation.updatedAt, to: &data)
        appendString(conversation.summary ?? "", to: &data)
        appendString(conversation.storagePath ?? "", to: &data)

        appendCount(conversation.messages.count, to: &data)
        for message in conversation.messages {
            appendString(message.id, to: &data)
            appendString(message.timestamp, to: &data)
            appendString(message.role, to: &data)
            appendString(message.content, to: &data)
            appendCount(message.toolCalls.count, to: &data)
            for tool in message.toolCalls {
                appendString(tool.id, to: &data)
                appendString(tool.name, to: &data)
                appendJSONValue(tool.input, to: &data)
                appendString(tool.output ?? "", to: &data)
                appendString(tool.status, to: &data)
            }
        }

        appendCount(conversation.fileChanges.count, to: &data)
        for change in conversation.fileChanges {
            appendString(change.path, to: &data)
            appendString(change.changeType, to: &data)
            appendString(change.timestamp, to: &data)
            appendString(change.messageId ?? "", to: &data)
        }
        return SHA256.hash(data: data)
            .map { String(format: "%02x", $0) }
            .joined()
    }

    private static func appendJSONValue(_ value: JSONValue, to data: inout Data) {
        switch value {
        case .null:
            appendASCII("n", to: &data)
        case .bool(let value):
            appendASCII(value ? "b1" : "b0", to: &data)
        case .number(let value):
            let raw = String(value.bitPattern, radix: 16)
            appendASCII("d" + String(repeating: "0", count: max(0, 16 - raw.count)) + raw, to: &data)
        case .string(let value):
            appendString(value, to: &data)
        case .array(let values):
            appendASCII("a", to: &data)
            appendCount(values.count, to: &data)
            for value in values {
                appendJSONValue(value, to: &data)
            }
        case .object(let values):
            appendASCII("o", to: &data)
            appendCount(values.count, to: &data)
            for key in values.keys.sorted(by: Self.utf8LessThan) {
                appendString(key, to: &data)
                if let value = values[key] {
                    appendJSONValue(value, to: &data)
                }
            }
        }
    }

    private static func appendCount(_ value: Int, to data: inout Data) {
        appendASCII("c\(value):", to: &data)
    }

    private static func appendString(_ value: String, to data: inout Data) {
        appendASCII("s\(value.lengthOfBytes(using: .utf8)):", to: &data)
        data.append(contentsOf: value.utf8)
    }

    private static func appendASCII(_ value: String, to data: inout Data) {
        data.append(contentsOf: value.utf8)
    }

    private static func utf8LessThan(_ left: String, _ right: String) -> Bool {
        left.utf8.lexicographicallyPrecedes(right.utf8)
    }

    private static func shouldReplace(
        existing: RemotePayload,
        with candidate: RemotePayload
    ) -> Bool {
        if isNewer(candidate.detail.updatedAt, than: existing.detail.updatedAt) {
            return true
        }
        if isNewer(existing.detail.updatedAt, than: candidate.detail.updatedAt) {
            return false
        }
        if candidate.priority != existing.priority {
            return candidate.priority > existing.priority
        }
        // A stable last tiebreaker avoids random overwrite/import behavior
        // when a cloud provider exposes duplicate legacy files in a new order.
        return candidate.stablePath < existing.stablePath
    }

    private static func isNewer(_ left: String, than right: String) -> Bool {
        guard let leftDate = parsedDate(left) else { return false }
        guard let rightDate = parsedDate(right) else { return true }
        return leftDate > rightDate
    }

    private static func parsedDate(_ value: String) -> Date? {
        let fractional = ISO8601DateFormatter()
        fractional.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        if let date = fractional.date(from: value) { return date }
        return ISO8601DateFormatter().date(from: value)
    }

    private static func rootURL(for folder: String) throws -> URL {
        let trimmed = folder.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else {
            throw NSError(
                domain: "NativeLocalSyncService",
                code: 1,
                userInfo: [NSLocalizedDescriptionKey: "本地同步文件夹不能为空。"]
            )
        }
        return URL(fileURLWithPath: trimmed, isDirectory: true).standardizedFileURL
    }

    private static func childURL(_ component: String, under root: URL) throws -> URL {
        try childURL([component], under: root)
    }

    private static func childURL(_ components: [String], under root: URL) throws -> URL {
        guard components.allSatisfy(isSafePathComponent) else {
            throw NSError(
                domain: "NativeLocalSyncService",
                code: 2,
                userInfo: [NSLocalizedDescriptionKey: "同步路径包含不安全的目录组件。"]
            )
        }
        var result = root.standardizedFileURL
        for component in components {
            result.appendPathComponent(component, isDirectory: false)
        }
        let canonicalRoot = root.standardizedFileURL.path
        let prefix = canonicalRoot.hasSuffix("/") ? canonicalRoot : canonicalRoot + "/"
        guard result.standardizedFileURL.path.hasPrefix(prefix) else {
            throw NSError(
                domain: "NativeLocalSyncService",
                code: 3,
                userInfo: [NSLocalizedDescriptionKey: "同步路径越出所选文件夹。"]
            )
        }
        return result
    }

    private static func isSafePathComponent(_ value: String) -> Bool {
        !value.isEmpty
            && value != "."
            && value != ".."
            && !value.contains("/")
            && !value.contains("\\")
            && !value.contains("\0")
    }

    private static func normalizedAgent(_ value: String) -> String? {
        let normalized = value.trimmingCharacters(in: .whitespacesAndNewlines)
            .lowercased()
        guard isSafePathComponent(normalized),
              normalized.unicodeScalars.allSatisfy({ scalar in
                  switch scalar.value {
                  case 48 ... 57, 65 ... 90, 97 ... 122, 45, 46, 95:
                      return true
                  default:
                      return false
                  }
              })
        else { return nil }
        return normalized
    }

    private static func isDirectory(_ url: URL) -> Bool {
        guard let values = try? url.resourceValues(forKeys: [.isDirectoryKey]) else {
            return false
        }
        return values.isDirectory == true
    }

    private static func isRegularFile(_ url: URL) -> Bool {
        guard let values = try? url.resourceValues(forKeys: [.isRegularFileKey]) else {
            return false
        }
        return values.isRegularFile == true
    }

    private static func isSymbolicLink(_ url: URL) -> Bool {
        (try? FileManager.default.destinationOfSymbolicLink(atPath: url.path)) != nil
    }

    private static func invalidConversationError(_ conversation: ConversationDetail) -> NSError {
        NSError(
            domain: "NativeLocalSyncService",
            code: 4,
            userInfo: [
                NSLocalizedDescriptionKey:
                    "对话 \(conversation.id) 的来源标识不适合写入同步目录。",
            ]
        )
    }

    private func hasActiveLockFiles(in root: URL) -> Bool {
        guard let enumerator = FileManager.default.enumerator(
            at: root,
            includingPropertiesForKeys: [.isDirectoryKey, .contentModificationDateKey],
            options: [.skipsPackageDescendants]
        ) else { return false }
        for case let url as URL in enumerator {
            let name = url.lastPathComponent
            if [".odrive", ".sync", ".tmp.driveupload"].contains(name) { return true }
            if name.hasPrefix("~$") { return true }
            let lower = name.lowercased()
            if [".tmp", ".partial", ".gdoc_tmp"].contains(where: lower.hasSuffix)
                || lower.contains(".crswap") {
                return true
            }
        }
        return false
    }

    /// Legacy macOS filename escaping. Readers accept it, but new files use
    /// `canonicalFilename(_:)` so Windows and macOS write the same path.
    static func idToFilename(_ id: String) -> String {
        id.replacingOccurrences(of: ":", with: "&#x3a;")
            .replacingOccurrences(of: "<", with: "&#x3c;")
            .replacingOccurrences(of: ">", with: "&#x3e;")
            .replacingOccurrences(of: "\"", with: "&#x22;")
            .replacingOccurrences(of: "|", with: "&#x7c;")
            .replacingOccurrences(of: "?", with: "&#x3f;")
            .replacingOccurrences(of: "*", with: "&#x2a;")
            .replacingOccurrences(of: "/", with: "&#x2f;")
            .replacingOccurrences(of: "\\", with: "&#x5c;")
    }

    static func canonicalFilename(_ id: String) -> String {
        Data(id.utf8).base64EncodedString()
            .trimmingCharacters(in: CharacterSet(charactersIn: "="))
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
    }

    private struct SyncKey: Hashable {
        let agent: String
        let id: String

        init?(agent: String, id: String) {
            guard let normalizedAgent = NativeLocalSyncService.normalizedAgent(agent),
                  !id.isEmpty,
                  !id.contains("\0") else { return nil }
            self.agent = normalizedAgent
            self.id = id
        }
    }

    private struct LocalPayload {
        let detail: ConversationDetail
        let hash: String
    }

    private struct RemotePayload {
        let detail: ConversationDetail
        let hash: String
        let priority: Int
        let stablePath: String
    }
}

struct NativeLocalSyncStatus: Sendable {
    let available: Bool
    let folderPath: String
    let remoteConversationCount: Int
    let lastSyncInfo: String?
}

struct NativeCloudReadiness: Sendable {
    let folderExists: Bool
    let isQuiet: Bool
    let hasLockFiles: Bool
    let recommendedAction: String
}

struct NativeLocalSyncResult: Sendable {
    let uploaded: Int
    let downloaded: Int
    let skipped: Int
    let conflictsResolved: Int
    let folderPath: String
}
