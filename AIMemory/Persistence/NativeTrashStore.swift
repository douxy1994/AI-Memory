import Foundation

/// AI Memory-owned trash metadata. Source histories are never inspected or
/// changed while listing trash records.
actor NativeTrashStore {
    private let root: URL
    private let conversations: NativeConversationStore
    private let sourceWriter: NativeAgentConversationWriter?
    private let fileManager: FileManager
    private let now: @Sendable () -> Date

    init(
        root: URL = DataPaths.trashDir,
        conversations: NativeConversationStore = NativeConversationStore(),
        sourceWriter: NativeAgentConversationWriter? = nil,
        fileManager: FileManager = .default,
        now: @escaping @Sendable () -> Date = Date.init
    ) {
        self.root = root
        self.conversations = conversations
        self.sourceWriter = sourceWriter
        self.fileManager = fileManager
        self.now = now
    }

    func list() throws -> [TrashRecord] {
        try removeExpiredRecords()
        guard FileManager.default.fileExists(atPath: root.path) else { return [] }
        guard let enumerator = FileManager.default.enumerator(
            at: root,
            includingPropertiesForKeys: [.isRegularFileKey],
            options: [.skipsHiddenFiles]
        ) else { return [] }

        var records: [TrashRecord] = []
        for case let url as URL in enumerator where url.pathExtension.lowercased() == "json" {
            let data = try Data(contentsOf: url)
            guard var object = try JSONSerialization.jsonObject(with: data) as? [String: Any] else {
                continue
            }
            object["record_path"] = url.path
            let normalized = try JSONSerialization.data(withJSONObject: object)
            if let record = try? JSONDecoder().decode(TrashRecord.self, from: normalized) {
                records.append(record)
            }
        }
        return records.sorted { $0.trashedAt > $1.trashedAt }
    }

    func trash(
        agent: String,
        id: String,
        retentionDays: Int,
        warnings customWarnings: [String]? = nil
    ) async throws -> NativeTrashResult {
        let conversation = try await conversations.readConversation(agent: agent, id: id)
        let now = now()
        let formatter = ISO8601DateFormatter()
        let expires = Calendar(identifier: .gregorian).date(
            byAdding: .day,
            value: min(365, max(1, retentionDays)),
            to: now
        ) ?? now
        let trashID = "\(agent)-\(id)-\(Int(now.timeIntervalSince1970 * 1000))"
        let agentRoot = root.appendingPathComponent(agent, isDirectory: true)
        try FileManager.default.createDirectory(
            at: agentRoot,
            withIntermediateDirectories: true
        )
        let recordURL = agentRoot.appendingPathComponent(Self.safeFileName(trashID) + ".json")
        let conversationData = try JSONEncoder().encode(conversation)
        let conversationObject = try JSONSerialization.jsonObject(with: conversationData)
        let warnings = customWarnings
            ?? (sourceWriter == nil
                ? ["原 agent 历史保持只读；AI Memory 仅移除自己的索引副本。"]
                : [])
        var resultWarnings = warnings
        var record: [String: Any] = [
            "schema_version": 1,
            "trash_id": trashID,
            "original_id": id,
            "source_agent": agent,
            "project_dir": conversation.projectDir,
            "summary": conversation.summary ?? NSNull(),
            "trashed_at": formatter.string(from: now),
            "expires_at": formatter.string(from: expires),
            "storage_path": conversation.storagePath ?? NSNull(),
            "resume_command": conversation.resumeCommand ?? NSNull(),
            "remote_backup_deleted": false,
            "remote_backup_path": NSNull(),
            "warnings": warnings,
            "conversation": conversationObject,
        ]
        let sourceMutation: SourceMutation?
        if sourceWriter != nil {
            sourceMutation = try planSourceMutation(
                conversation,
                recordURL: recordURL
            )
            if let sourceMutation {
                record["source_mutation"] = sourceMutation.kind
                record["source_backup_path"] =
                    sourceMutation.backupPath ?? NSNull()
                record["source_original_path"] =
                    sourceMutation.originalPath ?? NSNull()
                if let warning = sourceMutation.warning {
                    resultWarnings.append(warning)
                    record["warnings"] = resultWarnings
                }
            }
        } else {
            sourceMutation = nil
        }
        try writeRecord(record, to: recordURL)
        do {
            try await conversations.deleteIndexedConversation(agent: agent, id: id)
        } catch {
            try? FileManager.default.removeItem(at: recordURL)
            throw error
        }
        if let sourceMutation {
            do {
                try await performSourceMutation(
                    conversation,
                    mutation: sourceMutation
                )
            } catch {
                try? await conversations.upsertConversation(conversation)
                try? fileManager.removeItem(at: recordURL)
                throw error
            }
        }
        return NativeTrashResult(
            trashID: trashID,
            originalID: id,
            restoredID: nil,
            sourceAgent: agent,
            warnings: resultWarnings
        )
    }

    func restore(trashID: String, agent: String) async throws -> NativeTrashResult {
        let url = try recordURL(trashID: trashID, agent: agent)
        let data = try Data(contentsOf: url)
        guard let object = try JSONSerialization.jsonObject(with: data) as? [String: Any],
              let conversationObject = object["conversation"] else {
            throw NativeTrashError.invalidRecord
        }
        let conversationData = try JSONSerialization.data(withJSONObject: conversationObject)
        let conversation = try JSONDecoder().decode(
            ConversationDetail.self,
            from: conversationData
        )
        let restored: ConversationDetail
        if let backupPath = object["source_backup_path"] as? String,
           let originalPath = object["source_original_path"] as? String,
           !backupPath.isEmpty,
           !originalPath.isEmpty {
            try restoreRawSource(
                backupURL: URL(fileURLWithPath: backupPath),
                originalURL: URL(fileURLWithPath: originalPath)
            )
            restored = conversation
        } else if (object["source_mutation"] as? String) == "native-writer",
                  let sourceWriter {
            let result = try await sourceWriter.restoreArchivedSource(conversation)
            restored = Self.restoredConversation(conversation, result: result)
        } else {
            restored = conversation
        }
        try await conversations.upsertConversation(restored)
        try FileManager.default.removeItem(at: url)
        return NativeTrashResult(
            trashID: trashID,
            originalID: conversation.id,
            restoredID: restored.id,
            sourceAgent: conversation.sourceAgent,
            warnings: []
        )
    }

    func delete(trashID: String, agent: String) throws {
        let url = try recordURL(trashID: trashID, agent: agent)
        try deleteSourceBackupReferenced(by: url)
        try fileManager.removeItem(at: url)
    }

    func empty() throws {
        guard FileManager.default.fileExists(atPath: root.path) else { return }
        for record in try list() {
            guard !record.recordPath.isEmpty else { continue }
            let url = URL(fileURLWithPath: record.recordPath)
            try deleteSourceBackupReferenced(by: url)
            try fileManager.removeItem(at: url)
        }
    }

    private func planSourceMutation(
        _ conversation: ConversationDetail,
        recordURL: URL
    ) throws -> SourceMutation {
        guard let agent = AgentKind(rawValue: conversation.sourceAgent) else {
            throw NativeTrashError.invalidRecord
        }
        switch agent {
        case .claude, .gemini, .antigravity, .zcode, .kimi:
            guard let storagePath = conversation.storagePath, !storagePath.isEmpty else {
                throw NativeAgentConversationWriterError.invalidStore(
                    "\(agent.label) 会话缺少原始存储路径。"
                )
            }
            var originalURL = URL(fileURLWithPath: storagePath)
            if agent == .kimi {
                // Kimi stores one conversation across state.json plus agent
                // wire files, so archive the complete session directory.
                originalURL.deleteLastPathComponent()
            }
            guard fileManager.fileExists(atPath: originalURL.path) else {
                throw NativeAgentConversationWriterError.missingStore(originalURL.path)
            }
            let backupURL = recordURL
                .deletingPathExtension()
                .appendingPathExtension("source-backup")
            return SourceMutation(
                kind: "raw-backup",
                backupPath: backupURL.path,
                originalPath: originalURL.path,
                warning: nil
            )
        case .codex, .opencode:
            return SourceMutation(
                kind: "native-writer",
                backupPath: nil,
                originalPath: nil,
                warning: nil
            )
        case .hermes:
            // ChatMem's Hermes adapter does not implement deletion or
            // writing. Preserve the source and make the reduced behavior
            // explicit instead of reporting a false source deletion.
            return SourceMutation(
                kind: "index-only",
                backupPath: nil,
                originalPath: nil,
                warning: "Hermes 不支持安全删除/恢复；源历史已保留，仅移除 AI Memory 索引。"
            )
        }
    }

    private func performSourceMutation(
        _ conversation: ConversationDetail,
        mutation: SourceMutation
    ) async throws {
        switch mutation.kind {
        case "raw-backup":
            guard let backupPath = mutation.backupPath,
                  let originalPath = mutation.originalPath else {
                throw NativeTrashError.invalidRecord
            }
            let backupURL = URL(fileURLWithPath: backupPath)
            let originalURL = URL(fileURLWithPath: originalPath)
            if fileManager.fileExists(atPath: backupURL.path) {
                try fileManager.removeItem(at: backupURL)
            }
            try fileManager.copyItem(at: originalURL, to: backupURL)
            do {
                try fileManager.removeItem(at: originalURL)
            } catch {
                try? fileManager.removeItem(at: backupURL)
                throw error
            }
        case "native-writer":
            guard let sourceWriter else {
                throw NativeTrashError.invalidRecord
            }
            try await sourceWriter.archiveSource(conversation)
        case "index-only":
            return
        default:
            throw NativeTrashError.invalidRecord
        }
    }

    private func restoreRawSource(
        backupURL: URL,
        originalURL: URL
    ) throws {
        guard fileManager.fileExists(atPath: backupURL.path) else {
            throw NativeTrashError.sourceBackupMissing(backupURL.path)
        }
        guard !fileManager.fileExists(atPath: originalURL.path) else {
            throw NativeTrashError.sourceRestoreConflict(originalURL.path)
        }
        try fileManager.createDirectory(
            at: originalURL.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        try fileManager.moveItem(at: backupURL, to: originalURL)
    }

    private func deleteSourceBackupReferenced(by recordURL: URL) throws {
        guard fileManager.fileExists(atPath: recordURL.path),
              let data = try? Data(contentsOf: recordURL),
              let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let backupPath = object["source_backup_path"] as? String,
              !backupPath.isEmpty,
              fileManager.fileExists(atPath: backupPath) else {
            return
        }
        try fileManager.removeItem(atPath: backupPath)
    }

    private func removeExpiredRecords() throws {
        guard fileManager.fileExists(atPath: root.path),
              let enumerator = fileManager.enumerator(
                at: root,
                includingPropertiesForKeys: [.isRegularFileKey],
                options: [.skipsHiddenFiles]
              ) else {
            return
        }
        let formatter = ISO8601DateFormatter()
        let cutoff = now()
        for case let url as URL in enumerator
            where url.pathExtension.lowercased() == "json" {
            guard let data = try? Data(contentsOf: url),
                  let object = try? JSONSerialization.jsonObject(with: data)
                    as? [String: Any],
                  let expires = object["expires_at"] as? String,
                  let expiresAt = formatter.date(from: expires),
                  expiresAt <= cutoff else {
                continue
            }
            try deleteSourceBackupReferenced(by: url)
            try fileManager.removeItem(at: url)
        }
    }

    private func writeRecord(_ record: [String: Any], to url: URL) throws {
        let body = try JSONSerialization.data(
            withJSONObject: record,
            options: [.prettyPrinted, .sortedKeys]
        )
        try body.write(to: url, options: [.atomic])
    }

    private static func restoredConversation(
        _ conversation: ConversationDetail,
        result: NativeAgentWriteResult
    ) -> ConversationDetail {
        ConversationDetail(
            id: result.id,
            sourceAgent: conversation.sourceAgent,
            projectDir: conversation.projectDir,
            createdAt: conversation.createdAt,
            updatedAt: conversation.updatedAt,
            summary: conversation.summary,
            storagePath: result.storagePath,
            resumeCommand: result.resumeCommand,
            messages: conversation.messages,
            fileChanges: conversation.fileChanges
        )
    }

    private func recordURL(trashID: String, agent: String) throws -> URL {
        let candidate = root
            .appendingPathComponent(agent, isDirectory: true)
            .appendingPathComponent(Self.safeFileName(trashID) + ".json")
        guard candidate.standardizedFileURL.path.hasPrefix(
            root.standardizedFileURL.path + "/"
        ), FileManager.default.fileExists(atPath: candidate.path) else {
            throw NativeTrashError.notFound
        }
        return candidate
    }

    private static func safeFileName(_ value: String) -> String {
        Data(value.utf8).base64EncodedString()
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
    }

    private struct SourceMutation {
        let kind: String
        let backupPath: String?
        let originalPath: String?
        let warning: String?
    }
}

struct NativeTrashResult: Sendable {
    let trashID: String
    let originalID: String?
    let restoredID: String?
    let sourceAgent: String?
    let warnings: [String]
}

enum NativeTrashError: LocalizedError {
    case notFound
    case invalidRecord
    case sourceBackupMissing(String)
    case sourceRestoreConflict(String)

    var errorDescription: String? {
        switch self {
        case .notFound: "未找到回收站记录。"
        case .invalidRecord: "回收站记录损坏，无法恢复。"
        case .sourceBackupMissing(let path):
            "源历史备份不存在，无法恢复：\(path)"
        case .sourceRestoreConflict(let path):
            "源历史位置已有同名数据，未覆盖现有内容：\(path)"
        }
    }
}
