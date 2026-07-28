import Foundation
import CryptoKit

actor NativeLocalSyncService {
    private let store: NativeConversationStore
    private let encoder = JSONEncoder()
    private let decoder = JSONDecoder()

    init(store: NativeConversationStore = NativeConversationStore()) {
        self.store = store
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
    }

    func status(folder: String) throws -> NativeLocalSyncStatus {
        let url = URL(fileURLWithPath: folder, isDirectory: true)
        var isDirectory: ObjCBool = false
        let exists = FileManager.default.fileExists(
            atPath: url.path,
            isDirectory: &isDirectory
        ) && isDirectory.boolValue
        guard exists else {
            return NativeLocalSyncStatus(
                available: false,
                folderPath: url.path,
                remoteConversationCount: 0,
                lastSyncInfo: nil
            )
        }
        let remote = try readRemote(folder: url)
        let manifest = url.appendingPathComponent("manifest.json")
        var lastSync: String?
        if let data = try? Data(contentsOf: manifest),
           let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any] {
            lastSync = object["last_synced_at"] as? String
        }
        return NativeLocalSyncStatus(
            available: true,
            folderPath: url.path,
            remoteConversationCount: remote.count,
            lastSyncInfo: lastSync
        )
    }

    func readiness(folder: String) -> NativeCloudReadiness {
        let url = URL(fileURLWithPath: folder, isDirectory: true)
        var isDirectory: ObjCBool = false
        guard FileManager.default.fileExists(atPath: url.path, isDirectory: &isDirectory),
              isDirectory.boolValue else {
            return NativeCloudReadiness(
                folderExists: false,
                isQuiet: true,
                hasLockFiles: false,
                recommendedAction: "folder_missing"
            )
        }
        let hasLocks = hasActiveLockFiles(in: url)
        let recentlyModified: Bool
        if let values = try? url.resourceValues(forKeys: [.contentModificationDateKey]),
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
        let root = URL(fileURLWithPath: folder, isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let conversationsRoot = root.appendingPathComponent(
            "conversations",
            isDirectory: true
        )
        try FileManager.default.createDirectory(
            at: conversationsRoot,
            withIntermediateDirectories: true
        )
        for agent in AgentKind.allCases {
            try FileManager.default.createDirectory(
                at: conversationsRoot.appendingPathComponent(
                    agent.rawValue,
                    isDirectory: true
                ),
                withIntermediateDirectories: true
            )
        }

        var local: [SyncKey: ConversationDetail] = [:]
        for agent in AgentKind.allCases {
            for summary in try await store.listConversations(agent: agent.rawValue) {
                if let detail = try? await store.readConversation(
                    agent: agent.rawValue,
                    id: summary.id
                ) {
                    local[SyncKey(agent: agent.rawValue, id: summary.id)] = detail
                }
            }
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
                try writeRemote(localConversation, key: key, root: conversationsRoot)
                uploaded += 1
            case (.none, .some(let remoteConversation)):
                try await store.upsertConversation(remoteConversation)
                downloaded += 1
            case (.some(let localConversation), .some(let remoteConversation)):
                if localConversation.updatedAt > remoteConversation.updatedAt {
                    try writeRemote(localConversation, key: key, root: conversationsRoot)
                    uploaded += 1
                    conflicts += 1
                } else if remoteConversation.updatedAt > localConversation.updatedAt {
                    try await store.upsertConversation(remoteConversation)
                    downloaded += 1
                    conflicts += 1
                } else if try contentHash(localConversation)
                    != contentHash(remoteConversation) {
                    try writeRemote(localConversation, key: key, root: conversationsRoot)
                    uploaded += 1
                    conflicts += 1
                } else {
                    skipped += 1
                }
            case (.none, .none):
                break
            }
        }

        let manifest: [String: Any] = [
            "schema_version": 2,
            "app_version": Bundle.main.object(
                forInfoDictionaryKey: "CFBundleShortVersionString"
            ) as? String ?? "0.1.0",
            "last_synced_at": ISO8601DateFormatter().string(from: Date()),
            "sync_direction": "bidirectional",
            "uploaded": uploaded,
            "downloaded": downloaded,
            "skipped": skipped,
            "conflicts_resolved": conflicts,
            "total_local": local.count,
            "total_remote": remote.count,
        ]
        let manifestData = try JSONSerialization.data(
            withJSONObject: manifest,
            options: [.prettyPrinted, .sortedKeys]
        )
        try manifestData.write(
            to: root.appendingPathComponent("manifest.json"),
            options: [.atomic]
        )
        return NativeLocalSyncResult(
            uploaded: uploaded,
            downloaded: downloaded,
            skipped: skipped,
            conflictsResolved: conflicts,
            folderPath: root.path
        )
    }

    private func readRemote(folder: URL) throws -> [SyncKey: ConversationDetail] {
        let root = folder.appendingPathComponent("conversations", isDirectory: true)
        guard FileManager.default.fileExists(atPath: root.path) else { return [:] }
        var result: [SyncKey: ConversationDetail] = [:]
        for agent in AgentKind.allCases {
            let agentRoot = root.appendingPathComponent(agent.rawValue, isDirectory: true)
            guard let files = try? FileManager.default.contentsOfDirectory(
                at: agentRoot,
                includingPropertiesForKeys: [.isRegularFileKey],
                options: [.skipsHiddenFiles]
            ) else { continue }
            for file in files where file.pathExtension.lowercased() == "json" {
                guard let data = try? Data(contentsOf: file),
                      let conversation = try? decoder.decode(ConversationDetail.self, from: data)
                else { continue }
                result[SyncKey(agent: agent.rawValue, id: conversation.id)] = conversation
            }
        }
        return result
    }

    private func writeRemote(
        _ conversation: ConversationDetail,
        key: SyncKey,
        root: URL
    ) throws {
        let data = try encoder.encode(conversation)
        let destination = root
            .appendingPathComponent(key.agent, isDirectory: true)
            .appendingPathComponent(Self.idToFilename(key.id) + ".json")
        try data.write(to: destination, options: [.atomic])
    }

    private func contentHash(_ conversation: ConversationDetail) throws -> String {
        SHA256.hash(data: try encoder.encode(conversation))
            .map { String(format: "%02x", $0) }
            .joined()
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

    private struct SyncKey: Hashable {
        let agent: String
        let id: String
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
