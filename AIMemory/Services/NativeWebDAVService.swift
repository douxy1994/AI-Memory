// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
import Foundation
import CryptoKit

/// WebDAV verification and conversation backup implemented with URLSession.
actor NativeWebDAVService {
    private let conversations: NativeConversationStore
    private let session: URLSession

    init(
        conversations: NativeConversationStore = NativeConversationStore(),
        session: URLSession? = nil
    ) {
        self.conversations = conversations
        if let session {
            self.session = session
        } else {
            let configuration = URLSessionConfiguration.ephemeral
            configuration.timeoutIntervalForRequest = 30
            configuration.timeoutIntervalForResource = 120
            self.session = URLSession(configuration: configuration)
        }
    }

    func verify(
        scheme: String? = nil,
        host: String,
        path: String,
        remotePath: String? = nil,
        username: String?,
        password: String?
    ) async throws -> NativeWebDAVVerification {
        let base = try collectionURL(
            scheme: scheme,
            host: host,
            path: path,
            remotePath: remotePath
        )
        let request = try authorizedRequest(
            url: base,
            method: "PROPFIND",
            username: username,
            password: password,
            depth: "0"
        )
        let (_, response) = try await session.data(for: request)
        let status = try httpStatus(response)
        guard (200..<300).contains(status) else {
            throw NativeWebDAVError.http(status, base.absoluteString)
        }
        return NativeWebDAVVerification(status: status, url: base.absoluteString)
    }

    func sync(
        scheme: String? = nil,
        host: String,
        path: String,
        remotePath: String? = nil,
        username: String?,
        password: String?,
        progress: (@Sendable (NativeWebDAVSyncProgress) async -> Void)? = nil
    ) async throws -> NativeWebDAVSyncResult {
        let root = try collectionURL(
            scheme: scheme,
            host: host,
            path: path,
            remotePath: remotePath
        )
        try await ensureCollection(root, username: username, password: password)
        let conversationsURL = root.appendingPathComponent("conversations", isDirectory: true)
        try await ensureCollection(
            conversationsURL,
            username: username,
            password: password
        )

        var uploaded = 0
        var downloaded = 0
        var skipped = 0
        var completed = 0
        var errors: [String] = []
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys, .withoutEscapingSlashes]

        var local: [WebDAVSyncKey: WebDAVLocalPayload] = [:]
        let localDetails = try await conversations.exportAllConversationsForSync()
        for detail in localDetails {
            do {
                let data = try encoder.encode(detail)
                local[WebDAVSyncKey(agent: detail.sourceAgent, id: detail.id)] =
                    WebDAVLocalPayload(
                        detail: detail,
                        data: data,
                        updatedAt: detail.updatedAt,
                        sha256: Self.contentHash(data),
                        semanticDigest: Self.semanticDigest(detail)
                    )
            } catch {
                errors.append(
                    "\(detail.sourceAgent) · \(detail.id): \(error.localizedDescription)"
                )
            }
        }

        let remoteManifest = try await loadManifest(
            root: root,
            username: username,
            password: password
        )
        let remote = Dictionary(
            remoteManifest.entries.map {
                (WebDAVSyncKey(agent: $0.agent, id: $0.id), $0)
            },
            uniquingKeysWith: { first, _ in first }
        )
        let allKeys = Set(local.keys).union(remote.keys).sorted {
            ($0.agent, $0.id) < ($1.agent, $1.id)
        }
        let total = allKeys.count
        await progress?(
            NativeWebDAVSyncProgress(
                uploadedCount: 0,
                downloadedCount: 0,
                skippedCount: 0,
                completedCount: 0,
                totalCount: total,
                currentAgent: nil,
                uploadingManifest: false
            )
        )

        var merged: [WebDAVSyncKey: WebDAVManifestEntry] = [:]
        var ensuredAgentFolders = Set<String>()

        for key in allKeys {
            do {
                switch (local[key], remote[key]) {
                case (.some(let localPayload), .none):
                    let entry = try await upload(
                        localPayload,
                        key: key,
                        conversationsURL: conversationsURL,
                        ensuredAgentFolders: &ensuredAgentFolders,
                        username: username,
                        password: password
                    )
                    merged[key] = entry
                    uploaded += 1

                case (.none, .some(let remoteEntry)):
                    let payload = try await download(
                        remoteEntry,
                        root: root,
                        username: username,
                        password: password
                    )
                    try await conversations.upsertConversation(payload.detail)
                    merged[key] = payload.entry
                    downloaded += 1

                case (.some(let localPayload), .some(let remoteEntry)):
                    let remoteSemanticDigest = Self.currentSemanticDigest(
                        remoteEntry.semanticDigest
                    )
                    if remoteSemanticDigest == localPayload.semanticDigest {
                        // The versioned semantic digest is shared by Swift and
                        // .NET, so equivalent JSON never overwrites itself only
                        // because the two serializers emit different bytes.
                        merged[key] = remoteEntry
                        skipped += 1
                    } else if Self.hashesEqual(remoteEntry.sha256, localPayload.sha256) {
                        // Preserve an unchanged legacy raw hash as-is. It is
                        // already conclusive and does not require a manifest
                        // rewrite merely to add a newer optional field.
                        merged[key] = remoteEntry
                        skipped += 1
                    } else if remoteSemanticDigest == nil {
                        // A schema-v1/v2 legacy entry has no semantic digest.
                        // Its timestamp alone is not evidence of equality: read
                        // the payload once so equal timestamps with different
                        // conversation content still follow normal conflict
                        // resolution rather than being silently skipped.
                        let payload = try await download(
                            remoteEntry,
                            root: root,
                            username: username,
                            password: password
                        )
                        if payload.entry.semanticDigest == localPayload.semanticDigest {
                            merged[key] = payload.entry
                            skipped += 1
                        } else if Self.isRemoteNewer(
                            remoteEntry.updatedAt,
                            than: localPayload.updatedAt
                        ) {
                            try await conversations.upsertConversation(payload.detail)
                            merged[key] = payload.entry
                            downloaded += 1
                        } else {
                            let entry = try await upload(
                                localPayload,
                                key: key,
                                conversationsURL: conversationsURL,
                                ensuredAgentFolders: &ensuredAgentFolders,
                                username: username,
                                password: password
                            )
                            merged[key] = entry
                            uploaded += 1
                        }
                    } else if Self.isRemoteNewer(
                        remoteEntry.updatedAt,
                        than: localPayload.updatedAt
                    ) {
                        let payload = try await download(
                            remoteEntry,
                            root: root,
                            username: username,
                            password: password
                        )
                        try await conversations.upsertConversation(payload.detail)
                        merged[key] = payload.entry
                        downloaded += 1
                    } else {
                        // For a genuine equal-timestamp content conflict keep
                        // the established local-wins policy. The semantic
                        // digest ensures this branch is not reached for merely
                        // differently formatted equivalent JSON.
                        let entry = try await upload(
                            localPayload,
                            key: key,
                            conversationsURL: conversationsURL,
                            ensuredAgentFolders: &ensuredAgentFolders,
                            username: username,
                            password: password
                        )
                        merged[key] = entry
                        uploaded += 1
                    }

                case (.none, .none):
                    break
                }
            } catch {
                if let existing = remote[key] {
                    merged[key] = existing
                }
                errors.append(
                    "\(key.agent) · \(key.id): \(error.localizedDescription)"
                )
            }

            completed += 1
            await progress?(
                NativeWebDAVSyncProgress(
                    uploadedCount: uploaded,
                    downloadedCount: downloaded,
                    skippedCount: skipped,
                    completedCount: completed,
                    totalCount: total,
                    currentAgent: key.agent,
                    uploadingManifest: false
                )
            )
        }

        let mergedEntries = merged.values.sorted {
            ($0.agent, $0.id) < ($1.agent, $1.id)
        }
        let manifestChanged = !remoteManifest.exists
            || remoteManifest.schemaVersion < 2
            || mergedEntries != remoteManifest.entries.sorted {
                ($0.agent, $0.id) < ($1.agent, $1.id)
            }
        if manifestChanged {
            await progress?(
                NativeWebDAVSyncProgress(
                    uploadedCount: uploaded,
                    downloadedCount: downloaded,
                    skippedCount: skipped,
                    completedCount: completed,
                    totalCount: total,
                    currentAgent: nil,
                    uploadingManifest: true
                )
            )
            let manifest: [String: Any] = [
                "schema_version": 2,
                "generated_at": ISO8601DateFormatter().string(from: Date()),
                "conversations": mergedEntries.map(\.dictionary),
            ]
            let manifestData = try JSONSerialization.data(
                withJSONObject: manifest,
                options: [.prettyPrinted, .sortedKeys]
            )
            try await put(
                data: manifestData,
                url: root.appendingPathComponent("manifest.json"),
                username: username,
                password: password
            )
        }

        return NativeWebDAVSyncResult(
            uploadedCount: uploaded,
            downloadedCount: downloaded,
            skippedCount: skipped,
            totalCount: total,
            manifestUploaded: manifestChanged,
            remoteURL: root.absoluteString,
            errors: errors
        )
    }

    private func upload(
        _ payload: WebDAVLocalPayload,
        key: WebDAVSyncKey,
        conversationsURL: URL,
        ensuredAgentFolders: inout Set<String>,
        username: String?,
        password: String?
    ) async throws -> WebDAVManifestEntry {
        let agentURL = conversationsURL.appendingPathComponent(
            key.agent,
            isDirectory: true
        )
        if ensuredAgentFolders.insert(key.agent).inserted {
            try await ensureCollection(
                agentURL,
                username: username,
                password: password
            )
        }
        let name = Self.safeFileName(key.id) + ".json"
        try await put(
            data: payload.data,
            url: agentURL.appendingPathComponent(name),
            username: username,
            password: password
        )
        return WebDAVManifestEntry(
            agent: key.agent,
            id: key.id,
            file: "conversations/\(key.agent)/\(name)",
            updatedAt: payload.updatedAt,
            sha256: payload.sha256
        )
    }

    private func download(
        _ entry: WebDAVManifestEntry,
        root: URL,
        username: String?,
        password: String?
    ) async throws -> WebDAVRemotePayload {
        guard let url = URL(string: entry.file, relativeTo: root)?.absoluteURL else {
            throw NativeWebDAVError.invalidURL
        }
        let data = try await get(
            url: url,
            username: username,
            password: password
        )
        let detail = try JSONDecoder().decode(ConversationDetail.self, from: data)
        return WebDAVRemotePayload(
            detail: detail,
            entry: entry.withDigests(
                sha256: Self.contentHash(data),
                semanticDigest: Self.semanticDigest(detail)
            )
        )
    }

    private func loadManifest(
        root: URL,
        username: String?,
        password: String?
    ) async throws -> WebDAVRemoteManifest {
        let url = root.appendingPathComponent("manifest.json")
        var request = try authorizedRequest(
            url: url,
            method: "GET",
            username: username,
            password: password
        )
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        let (data, response) = try await session.data(for: request)
        let status = try httpStatus(response)
        if status == 404 {
            return WebDAVRemoteManifest(exists: false, schemaVersion: 0, entries: [])
        }
        guard (200..<300).contains(status) else {
            throw NativeWebDAVError.http(status, url.absoluteString)
        }
        guard let rootObject = try JSONSerialization.jsonObject(with: data) as? [String: Any]
        else {
            throw NativeWebDAVError.invalidManifest
        }
        let entries = (rootObject["conversations"] as? [[String: Any]] ?? []).compactMap {
            WebDAVManifestEntry(dictionary: $0)
        }
        return WebDAVRemoteManifest(
            exists: true,
            schemaVersion: rootObject["schema_version"] as? Int ?? 1,
            entries: entries
        )
    }

    private func get(
        url: URL,
        username: String?,
        password: String?
    ) async throws -> Data {
        let request = try authorizedRequest(
            url: url,
            method: "GET",
            username: username,
            password: password
        )
        let (data, response) = try await session.data(for: request)
        let status = try httpStatus(response)
        guard (200..<300).contains(status) else {
            throw NativeWebDAVError.http(status, url.absoluteString)
        }
        return data
    }

    private func ensureCollection(
        _ url: URL,
        username: String?,
        password: String?
    ) async throws {
        let probe = try authorizedRequest(
            url: url,
            method: "PROPFIND",
            username: username,
            password: password,
            depth: "0"
        )
        let (_, probeResponse) = try await session.data(for: probe)
        let probeStatus = try httpStatus(probeResponse)
        if (200..<300).contains(probeStatus) { return }
        guard probeStatus == 404 else {
            throw NativeWebDAVError.http(probeStatus, url.absoluteString)
        }

        let create = try authorizedRequest(
            url: url,
            method: "MKCOL",
            username: username,
            password: password
        )
        let (_, createResponse) = try await session.data(for: create)
        let createStatus = try httpStatus(createResponse)
        guard (200..<300).contains(createStatus) || createStatus == 405 else {
            throw NativeWebDAVError.http(createStatus, url.absoluteString)
        }
    }

    private func put(
        data: Data,
        url: URL,
        username: String?,
        password: String?
    ) async throws {
        var request = try authorizedRequest(
            url: url,
            method: "PUT",
            username: username,
            password: password
        )
        request.setValue("application/json; charset=utf-8", forHTTPHeaderField: "Content-Type")
        let (_, response) = try await session.upload(for: request, from: data)
        let status = try httpStatus(response)
        guard (200..<300).contains(status) else {
            throw NativeWebDAVError.http(status, url.absoluteString)
        }
    }

    private func authorizedRequest(
        url: URL,
        method: String,
        username: String?,
        password: String?,
        depth: String? = nil
    ) throws -> URLRequest {
        let user = username?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        let secret = password ?? ""
        guard !user.isEmpty, !secret.isEmpty else {
            throw NativeWebDAVError.missingCredentials
        }
        var request = URLRequest(url: url)
        request.httpMethod = method
        request.setValue(
            "Basic " + Data("\(user):\(secret)".utf8).base64EncodedString(),
            forHTTPHeaderField: "Authorization"
        )
        if let depth { request.setValue(depth, forHTTPHeaderField: "Depth") }
        return request
    }

    private func collectionURL(
        scheme: String?,
        host: String,
        path: String,
        remotePath: String?
    ) throws -> URL {
        let trimmedHost = host.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmedHost.isEmpty else { throw NativeWebDAVError.invalidURL }
        let baseText = trimmedHost.contains("://")
            ? trimmedHost
            : "\((scheme == "http") ? "http" : "https")://\(trimmedHost)"
        guard var components = URLComponents(string: baseText),
              components.host != nil else {
            throw NativeWebDAVError.invalidURL
        }
        let suffix = [path, remotePath ?? ""]
            .map { $0.trimmingCharacters(in: CharacterSet(charactersIn: "/")) }
            .filter { !$0.isEmpty }
            .joined(separator: "/")
        var basePath = components.percentEncodedPath
        if !basePath.hasSuffix("/") { basePath += "/" }
        if !suffix.isEmpty {
            basePath += suffix.split(separator: "/").map {
                String($0).addingPercentEncoding(withAllowedCharacters: .urlPathAllowed)
                    ?? String($0)
            }.joined(separator: "/")
            basePath += "/"
        }
        components.percentEncodedPath = basePath
        guard let url = components.url else { throw NativeWebDAVError.invalidURL }
        return url
    }

    private func httpStatus(_ response: URLResponse) throws -> Int {
        guard let response = response as? HTTPURLResponse else {
            throw NativeWebDAVError.nonHTTPResponse
        }
        return response.statusCode
    }

    private static func safeFileName(_ id: String) -> String {
        Data(id.utf8).base64EncodedString()
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
    }

    nonisolated static func contentHash(_ data: Data) -> String {
        SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
    }

    /// Stable across the native Swift and Windows .NET implementations.
    /// This deliberately covers only conversation state that both stores
    /// persist; generated resume commands and non-persisted message metadata
    /// are excluded so an import/export round trip does not manufacture a
    /// false content conflict.
    nonisolated static func semanticDigest(_ conversation: ConversationDetail) -> String {
        WebDAVSemanticDigest.digest(conversation)
    }

    nonisolated private static func currentSemanticDigest(_ value: String?) -> String? {
        guard let value,
              value.hasPrefix(WebDAVSemanticDigest.prefix) else {
            return nil
        }
        return value
    }

    nonisolated private static func hashesEqual(_ lhs: String?, _ rhs: String) -> Bool {
        guard let lhs else { return false }
        return lhs.caseInsensitiveCompare(rhs) == .orderedSame
    }

    nonisolated private static func isRemoteNewer(_ remote: String, than local: String) -> Bool {
        Self.syncDate(remote) > Self.syncDate(local)
    }

    nonisolated private static func syncDate(_ value: String) -> Date {
        let fractional = ISO8601DateFormatter()
        fractional.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return fractional.date(from: value)
            ?? ISO8601DateFormatter().date(from: value)
            ?? .distantPast
    }
}

private struct WebDAVSyncKey: Hashable {
    let agent: String
    let id: String
}

private struct WebDAVLocalPayload {
    let detail: ConversationDetail
    let data: Data
    let updatedAt: String
    let sha256: String
    let semanticDigest: String
}

private struct WebDAVRemotePayload {
    let detail: ConversationDetail
    let entry: WebDAVManifestEntry
}

private struct WebDAVRemoteManifest {
    let exists: Bool
    let schemaVersion: Int
    let entries: [WebDAVManifestEntry]
}

private struct WebDAVManifestEntry: Equatable {
    let agent: String
    let id: String
    let file: String
    let updatedAt: String
    /// Raw payload bytes hash retained for schema-v2 / legacy clients.
    let sha256: String?
    /// `aimemory-conversation-v1:<sha256>` shared semantic digest.
    let semanticDigest: String?

    init?(
        dictionary: [String: Any]
    ) {
        guard let agent = dictionary["agent"] as? String,
              let id = dictionary["id"] as? String,
              let file = dictionary["file"] as? String,
              let updatedAt = dictionary["updated_at"] as? String else {
            return nil
        }
        self.agent = agent
        self.id = id
        self.file = file
        self.updatedAt = updatedAt
        sha256 = dictionary["sha256"] as? String
        semanticDigest = dictionary["semantic_digest"] as? String
    }

    init(
        agent: String,
        id: String,
        file: String,
        updatedAt: String,
        sha256: String?,
        semanticDigest: String? = nil
    ) {
        self.agent = agent
        self.id = id
        self.file = file
        self.updatedAt = updatedAt
        self.sha256 = sha256
        self.semanticDigest = semanticDigest
    }

    var dictionary: [String: Any] {
        var value: [String: Any] = [
            "agent": agent,
            "id": id,
            "file": file,
            "updated_at": updatedAt,
        ]
        if let sha256 { value["sha256"] = sha256 }
        if let semanticDigest { value["semantic_digest"] = semanticDigest }
        return value
    }

    func withDigests(
        sha256: String?,
        semanticDigest: String?
    ) -> WebDAVManifestEntry {
        WebDAVManifestEntry(
            agent: agent,
            id: id,
            file: file,
            updatedAt: updatedAt,
            sha256: sha256,
            semanticDigest: semanticDigest
        )
    }
}

private enum WebDAVSemanticTag: UInt8 {
    case null = 0
    case falseValue = 1
    case trueValue = 2
    case number = 3
    case string = 4
    case array = 5
    case object = 6
}

/// Versioned, binary-framed canonical representation shared with
/// `AIMemory.Core.Services.WebDavService`. It intentionally does not reuse
/// either platform's JSON serializer because byte-for-byte JSON output differs
/// between Foundation and System.Text.Json even for the same conversation.
private enum WebDAVSemanticDigest {
    static let prefix = "aimemory-conversation-v1:"
    private static let magic = Data("aimemory-conversation-semantic-v1\0".utf8)

    static func digest(_ conversation: ConversationDetail) -> String {
        var writer = Writer(magic: magic)
        writer.writeString(conversation.id)
        writer.writeString(conversation.sourceAgent)
        writer.writeString(conversation.projectDir)
        writer.writeString(conversation.createdAt)
        writer.writeString(conversation.updatedAt)
        writer.writePersistentOptionalString(conversation.summary)
        writer.writePersistentOptionalString(conversation.storagePath)

        writer.writeArrayCount(conversation.messages.count)
        for message in conversation.messages {
            writer.writeString(message.id)
            writer.writeString(message.timestamp)
            writer.writeString(message.role)
            writer.writeString(message.content)
            writer.writeArrayCount(message.toolCalls.count)
            for tool in message.toolCalls {
                writer.writeString(tool.id)
                writer.writeString(tool.name)
                writer.writeJSONValue(tool.input)
                writer.writePersistentOptionalString(tool.output)
                writer.writeString(tool.status)
            }
        }

        writer.writeArrayCount(conversation.fileChanges.count)
        for change in conversation.fileChanges {
            writer.writeString(change.path)
            writer.writeString(change.changeType)
            writer.writeString(change.timestamp)
            writer.writePersistentOptionalString(change.messageId)
        }
        return prefix + writer.finish()
    }

    private struct Writer {
        private var hasher = SHA256()

        init(magic: Data) {
            hasher.update(data: magic)
        }

        mutating func writePersistentOptionalString(_ value: String?) {
            guard let value, !value.isEmpty else {
                writeNull()
                return
            }
            writeString(value)
        }

        mutating func writeNull() {
            writeTag(.null)
        }

        mutating func writeString(_ value: String) {
            let bytes = Data(value.utf8)
            writeTag(.string)
            writeUInt64(UInt64(bytes.count))
            hasher.update(data: bytes)
        }

        mutating func writeArrayCount(_ count: Int) {
            writeTag(.array)
            writeUInt64(UInt64(count))
        }

        mutating func writeJSONValue(_ value: JSONValue) {
            switch value {
            case .null:
                writeNull()
            case .bool(false):
                writeTag(.falseValue)
            case .bool(true):
                writeTag(.trueValue)
            case .number(let value):
                writeTag(.number)
                // JSON numbers are decoded as IEEE-754 doubles by Swift and
                // .NET. Normalize -0 so equivalent zero values share a digest.
                writeUInt64(value == 0 ? 0 : value.bitPattern)
            case .string(let value):
                writeString(value)
            case .array(let values):
                writeArrayCount(values.count)
                for value in values {
                    writeJSONValue(value)
                }
            case .object(let values):
                writeTag(.object)
                let keys = values.keys.sorted(by: WebDAVSemanticDigest.utf8Less)
                writeUInt64(UInt64(keys.count))
                for key in keys {
                    writeString(key)
                    if let value = values[key] {
                        writeJSONValue(value)
                    } else {
                        writeNull()
                    }
                }
            }
        }

        mutating func finish() -> String {
            hasher.finalize().map { String(format: "%02x", $0) }.joined()
        }

        private mutating func writeTag(_ tag: WebDAVSemanticTag) {
            hasher.update(data: Data([tag.rawValue]))
        }

        private mutating func writeUInt64(_ value: UInt64) {
            var bigEndian = value.bigEndian
            let data = withUnsafeBytes(of: &bigEndian) { Data($0) }
            hasher.update(data: data)
        }
    }

    private static func utf8Less(_ lhs: String, _ rhs: String) -> Bool {
        let left = Array(lhs.utf8)
        let right = Array(rhs.utf8)
        let sharedCount = Swift.min(left.count, right.count)
        for index in 0..<sharedCount where left[index] != right[index] {
            return left[index] < right[index]
        }
        return left.count < right.count
    }
}

struct NativeWebDAVVerification: Sendable {
    let status: Int
    let url: String
}

struct NativeWebDAVSyncResult: Sendable {
    let uploadedCount: Int
    let downloadedCount: Int
    let skippedCount: Int
    let totalCount: Int
    let manifestUploaded: Bool
    let remoteURL: String
    let errors: [String]
}

struct NativeWebDAVSyncProgress: Sendable, Equatable {
    let uploadedCount: Int
    let downloadedCount: Int
    let skippedCount: Int
    let completedCount: Int
    let totalCount: Int
    let currentAgent: String?
    let uploadingManifest: Bool
}

enum NativeWebDAVError: LocalizedError {
    case invalidURL
    case invalidManifest
    case missingCredentials
    case nonHTTPResponse
    case http(Int, String)

    var errorDescription: String? {
        switch self {
        case .invalidURL:
            "WebDAV 地址无效。"
        case .invalidManifest:
            "WebDAV 远端清单格式无效。"
        case .missingCredentials:
            "缺少 WebDAV 用户名或密码。"
        case .nonHTTPResponse:
            "WebDAV 服务器未返回 HTTP 响应。"
        case .http(let status, let url):
            "WebDAV 服务器返回 HTTP \(status)：\(url)"
        }
    }
}
