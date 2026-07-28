import Foundation

/// Native service façade used by the app store.
///
/// The type name is kept for source compatibility with the existing SwiftUI
/// layer, but every operation is implemented by Swift actors and Apple
/// frameworks. No external bridge process is launched.
@MainActor
final class BridgeClient: ObservableObject {
    let telemetry = Telemetry()

    private let decoder = JSONDecoder()
    private let nativeConversations = NativeConversationStore()
    private let nativeSourceWriter = NativeAgentConversationWriter()
    private lazy var nativeTrash = NativeTrashStore(
        conversations: nativeConversations,
        sourceWriter: nativeSourceWriter
    )
    private let nativeCredentials = NativeCredentialStore()
    private let nativeWebDAV = NativeWebDAVService()
    private let nativeLocalSync = NativeLocalSyncService()
    private let nativeMigration = NativeMigrationService()
    private let nativeHistory = NativeHistoryImporter()
    private let nativeSettings = NativeSettingsStore()
    private let nativeIntegrations = NativeAgentIntegrationStore()

    init() {}

    // MARK: - Typed convenience methods

    func ping() async throws -> Bool {
        true
    }

    func detectSources() async throws -> [ConversationSourceStatus] {
        try await nativeConversations.detectSources()
    }

    func listConversations(agent: String) async throws -> [ConversationSummary] {
        try await nativeConversations.listConversations(agent: agent)
    }

    func searchConversations(agent: String, query: String) async throws -> [ConversationSummary] {
        try await nativeConversations.searchConversations(agent: agent, text: query)
    }

    func readConversation(agent: String, id: String) async throws -> ConversationDetail {
        try await nativeConversations.readConversation(agent: agent, id: id)
    }

    func refreshLocalHistory(agent: AgentKind) async -> NativeHistoryImportReport {
        await nativeHistory.importAgent(agent)
    }

    func autoCaptureConversation(
        agent: AgentKind,
        id: String,
        repoRoot: String
    ) async throws -> (detail: ConversationDetail, checkpoint: Checkpoint) {
        _ = await nativeHistory.importAgent(agent)
        let detail = try await nativeConversations.readConversation(
            agent: agent.rawValue,
            id: id
        )
        let effectiveRepoRoot = detail.projectDir.trimmingCharacters(
            in: .whitespacesAndNewlines
        ).isEmpty ? repoRoot : detail.projectDir
        guard !effectiveRepoRoot.trimmingCharacters(
            in: .whitespacesAndNewlines
        ).isEmpty else {
            throw NativeConversationStoreError.database(
                "对话没有可用的项目路径。"
            )
        }
        try await nativeConversations.upsertConversation(detail)
        let capturedAt = ISO8601DateFormatter().string(from: Date())
        let metadata: [String: Any] = [
            "capture": "auto",
            "captured_at": capturedAt,
            "storage_path": detail.storagePath ?? "",
            "message_count": detail.messages.count,
            "file_count": detail.fileChanges.count,
            "source_conversation_id": detail.id,
        ]
        let metadataData = try JSONSerialization.data(
            withJSONObject: metadata,
            options: [.sortedKeys]
        )
        guard let metadataJSON = String(
            data: metadataData,
            encoding: .utf8
        ) else {
            throw NativeConversationStoreError.database(
                "无法编码自动恢复点 metadata。"
            )
        }
        let checkpoint = try await nativeConversations.upsertAutoCheckpoint(
            repoRoot: effectiveRepoRoot,
            conversationID: "\(agent.rawValue):\(detail.id)",
            sourceAgent: agent.rawValue,
            summary: detail.summary ?? detail.id,
            resumeCommand: detail.resumeCommand,
            metadataJSON: metadataJSON
        )
        return (detail, checkpoint)
    }

    func getRepoMemoryHealth(repoRoot: String) async throws -> RepoHealth {
        try await nativeConversations.repoHealth(repoRoot: repoRoot)
    }

    func listMemoryCandidates(repoRoot: String) async throws -> [MemoryCandidate] {
        try await nativeConversations.listMemoryCandidates(repoRoot: repoRoot)
    }

    func listRepoMemories(repoRoot: String) async throws -> [ApprovedMemory] {
        try await nativeConversations.listApprovedMemories(repoRoot: repoRoot)
    }

    func listWikiPages(repoRoot: String) async throws -> [WikiPage] {
        try await nativeConversations.listWikiPages(repoRoot: repoRoot)
    }

    func listCheckpoints(repoRoot: String) async throws -> [Checkpoint] {
        try await nativeConversations.listCheckpoints(repoRoot: repoRoot)
    }

    func listHandoffs(repoRoot: String) async throws -> [HandoffPacket] {
        try await nativeConversations.listHandoffs(repoRoot: repoRoot)
    }

    func listActiveRuns(repoRoot: String) async throws -> [AgentRunRecord] {
        try await nativeConversations.listActiveRuns(repoRoot: repoRoot)
    }

    func listRunArtifacts(repoRoot: String) async throws -> [RunArtifactRecord] {
        try await nativeConversations.listRunArtifacts(repoRoot: repoRoot)
    }

    func listEpisodes(repoRoot: String) async throws -> [EpisodeRecord] {
        try await nativeConversations.listEpisodes(repoRoot: repoRoot)
    }

    func listMemoryConflicts(repoRoot: String) async throws -> [MemoryConflictRecord] {
        try await nativeConversations.listMemoryConflicts(repoRoot: repoRoot, status: nil)
    }

    func listEntityGraph(repoRoot: String, limit: Int = 25) async throws -> MemoryEntityGraph {
        try await nativeConversations.listEntityGraph(repoRoot: repoRoot, limit: limit)
    }

    func resumeFromCheckpoint(
        checkpointID: String,
        toAgent: String,
        targetProfile: String? = nil
    ) async throws -> HandoffPacket {
        try await nativeConversations.resumeFromCheckpoint(
            checkpointID: checkpointID,
            toAgent: toAgent,
            targetProfile: targetProfile
        )
    }

    func listTrashedConversations() async throws -> [TrashRecord] {
        try await nativeTrash.list()
    }

    func listReposWithCandidates() async throws -> [RepoCandidateCount] {
        try await nativeConversations.listReposWithCandidates()
    }

    // MARK: - Phase C: trash write paths

    func trashConversation(agent: String, id: String, retentionDays: Int = 14) async throws -> TrashActionResult {
        let result = try await nativeTrash.trash(
            agent: agent,
            id: id,
            retentionDays: retentionDays
        )
        return try decodeTrashResult(result)
    }

    func restoreTrashed(trashID: String, agent: String) async throws -> TrashActionResult {
        let result = try await nativeTrash.restore(trashID: trashID, agent: agent)
        return try decodeTrashResult(result)
    }

    func deleteTrashRecord(trashID: String, agent: String) async throws {
        try await nativeTrash.delete(trashID: trashID, agent: agent)
    }

    func emptyTrash() async throws {
        try await nativeTrash.empty()
    }

    private func decodeTrashResult(_ result: NativeTrashResult) throws -> TrashActionResult {
        var object: [String: Any] = [
            "trash_id": result.trashID,
            "warnings": result.warnings,
        ]
        if let value = result.originalID { object["original_id"] = value }
        if let value = result.restoredID { object["restored_id"] = value }
        if let value = result.sourceAgent { object["source_agent"] = value }
        return try decoder.decode(
            TrashActionResult.self,
            from: JSONSerialization.data(withJSONObject: object)
        )
    }

    // MARK: - Phase C: migrate

    func migrateConversation(source: String, target: String, id: String, mode: String) async throws -> MigrationResult {
        let result = try await nativeMigration.migrate(
            source: source,
            target: target,
            id: id,
            mode: mode
        )
        let object: [String: Any] = [
            "new_id": result.newID,
            "source": result.source,
            "target": result.target,
            "mode": result.mode,
            "verified": result.verified,
            "source_message_count": result.sourceMessageCount,
            "target_message_count": result.targetMessageCount,
            "source_file_count": result.sourceFileCount,
            "first_user_preserved": result.firstUserPreserved,
            "cut_deleted_source": result.cutDeletedSource,
            "warnings": result.warnings,
        ]
        return try decoder.decode(
            MigrationResult.self,
            from: JSONSerialization.data(withJSONObject: object)
        )
    }

    // MARK: - Phase C: agent integration

    func detectAgentIntegrations() async throws -> [AgentIntegrationStatus] {
        await nativeIntegrations.detect()
    }

    func installAgentIntegration(agent: String) async throws -> [String: Any] {
        try await nativeIntegrations.install(agent: agent).dictionary()
    }

    func uninstallAgentIntegration(agent: String) async throws -> [String: Any] {
        try await nativeIntegrations.uninstall(agent: agent).dictionary()
    }

    func saveAppSettings(_ settings: [String: Any]) async throws {
        let data = try JSONSerialization.data(withJSONObject: settings)
        let preferences = try JSONDecoder().decode(AppPreferences.self, from: data)
        try await nativeSettings.save(preferences)
    }

    // MARK: - Phase C: local folder sync (OneDrive / Google Drive / Dropbox)

    func localSyncStatus(folder: String) async throws -> [String: Any] {
        let result = try await nativeLocalSync.status(folder: folder)
        var dictionary: [String: Any] = [
            "available": result.available,
            "folder_path": result.folderPath,
            "remote_conversation_count": result.remoteConversationCount,
        ]
        if let value = result.lastSyncInfo { dictionary["last_sync_info"] = value }
        return dictionary
    }

    func checkCloudReadiness(folder: String) async throws -> [String: Any] {
        let result = await nativeLocalSync.readiness(folder: folder)
        return [
            "folder_exists": result.folderExists,
            "is_quiet": result.isQuiet,
            "has_lock_files": result.hasLockFiles,
            "recommended_action": result.recommendedAction,
        ]
    }

    func syncLocalNow(folder: String) async throws -> [String: Any] {
        let result = try await nativeLocalSync.sync(folder: folder)
        return [
            "uploaded": result.uploaded,
            "downloaded": result.downloaded,
            "skipped": result.skipped,
            "conflicts_resolved": result.conflictsResolved,
            "folder_path": result.folderPath,
        ]
    }

    // MARK: - Phase C: WebDAV sync

    func verifyWebDAVServer(
        scheme: String? = nil,
        host: String,
        path: String,
        remotePath: String? = nil,
        username: String?,
        password: String?
    ) async throws -> [String: Any] {
        let result = try await nativeWebDAV.verify(
            scheme: scheme,
            host: host,
            path: path,
            remotePath: remotePath,
            username: username,
            password: password
        )
        return ["ok": true, "status": result.status, "url": result.url]
    }

    func syncWebDAVNow(
        scheme: String? = nil,
        host: String,
        path: String,
        remotePath: String? = nil,
        username: String?,
        password: String?,
        progress: (@Sendable (NativeWebDAVSyncProgress) async -> Void)? = nil
    ) async throws -> [String: Any] {
        let result = try await nativeWebDAV.sync(
            scheme: scheme,
            host: host,
            path: path,
            remotePath: remotePath,
            username: username,
            password: password,
            progress: progress
        )
        return [
            "uploaded_count": result.uploadedCount,
            "downloaded_count": result.downloadedCount,
            "skipped_count": result.skippedCount,
            "total_count": result.totalCount,
            "manifest_uploaded": result.manifestUploaded,
            "remote_url": result.remoteURL,
            "errors": result.errors,
        ]
    }

    func saveWebDAVPassword(username: String, password: String) async throws {
        try await nativeCredentials.save(password: password, account: username)
    }

    func loadWebDAVPassword(username: String) async throws -> String? {
        try await nativeCredentials.load(account: username)
    }

    func runUpgradeReadinessCheck() async -> UpgradeReadinessReport {
        var checks: [UpgradeReadinessCheck] = []
        let settings: AppPreferences?
        do {
            settings = try await nativeSettings.load()
            checks.append(UpgradeReadinessCheck(
                key: "settings",
                label: "原生设置文件",
                status: "ok",
                detail: FileManager.default.fileExists(
                    atPath: DataPaths.settingsURL.path
                )
                    ? "设置文件存在且可以解析。"
                    : "尚未生成设置文件；当前使用安全默认值。"
            ))
        } catch {
            settings = nil
            checks.append(UpgradeReadinessCheck(
                key: "settings",
                label: "原生设置文件",
                status: "error",
                detail: "设置文件无法解析：\(error.localizedDescription)"
            ))
        }

        if let sync = settings?.sync, sync.provider == "webdav" {
            let complete = !sync.webdavHost.trimmingCharacters(
                in: .whitespacesAndNewlines
            ).isEmpty
                && !sync.username.trimmingCharacters(
                    in: .whitespacesAndNewlines
                ).isEmpty
                && !sync.remotePath.trimmingCharacters(
                    in: .whitespacesAndNewlines
                ).isEmpty
            checks.append(UpgradeReadinessCheck(
                key: "webdav_profile",
                label: "WebDAV 配置",
                status: complete ? "ok" : "warning",
                detail: complete
                    ? "服务器、用户名和远程目录均已配置。"
                    : "WebDAV 已启用，但服务器、用户名或远程目录不完整。"
            ))

            do {
                let password = try await nativeCredentials.load(
                    account: sync.username
                )
                let present = password?.isEmpty == false
                checks.append(UpgradeReadinessCheck(
                    key: "webdav_password",
                    label: "WebDAV 密码",
                    status: present ? "ok" : "warning",
                    detail: present
                        ? "密码存在于 AI Memory 钥匙串。"
                        : "钥匙串中没有对应密码，请重新输入并验证服务器。"
                ))
            } catch {
                checks.append(UpgradeReadinessCheck(
                    key: "webdav_password",
                    label: "WebDAV 密码",
                    status: "warning",
                    detail: "无法读取钥匙串：\(error.localizedDescription)"
                ))
            }
        } else {
            checks.append(UpgradeReadinessCheck(
                key: "webdav_profile",
                label: "WebDAV 配置",
                status: "ok",
                detail: "当前未启用 WebDAV 同步。"
            ))
            checks.append(UpgradeReadinessCheck(
                key: "webdav_password",
                label: "WebDAV 密码",
                status: "ok",
                detail: "当前同步模式不需要 WebDAV 密码。"
            ))
        }

        do {
            let database = try NativeDatabase()
            _ = try await database.currentSchemaVersion()
            checks.append(UpgradeReadinessCheck(
                key: "memory_store",
                label: "记忆数据库",
                status: "ok",
                detail: "数据库可以打开，结构版本有效。"
            ))
        } catch {
            checks.append(UpgradeReadinessCheck(
                key: "memory_store",
                label: "记忆数据库",
                status: "error",
                detail: "数据库无法打开：\(error.localizedDescription)"
            ))
        }

        let errors = checks.filter { $0.status == "error" }
        let warnings = checks.filter { $0.status == "warning" }
        let status = !errors.isEmpty ? "error" : (!warnings.isEmpty ? "warning" : "ok")
        let summary: String
        if !errors.isEmpty {
            summary = "发现 \(errors.count) 个阻断问题。"
        } else if !warnings.isEmpty {
            summary = "发现 \(warnings.count) 个需要处理的项目。"
        } else {
            summary = "升级就绪检查通过。"
        }
        return UpgradeReadinessReport(
            status: status,
            summary: summary,
            checks: checks,
            warnings: checks.filter { $0.status != "ok" }.map(\.detail)
        )
    }

    // MARK: - Phase C write paths (memory governance)

    /// Review a candidate: approve / approve_with_edit / approve_merge / reject / snooze.
    func reviewMemoryCandidate(candidateID: String, action: String,
                               title: String = "", value: String = "",
                               usageHint: String = "", memoryID: String = "") async throws {
        try await nativeConversations.reviewCandidate(
            id: candidateID,
            action: action,
            title: title,
            value: value,
            usageHint: usageHint,
            targetMemoryID: memoryID
        )
    }

    func retireMemory(memoryID: String) async throws {
        try await nativeConversations.retireMemory(id: memoryID)
    }

    func reverifyMemory(memoryID: String) async throws {
        try await nativeConversations.reverifyMemory(id: memoryID)
    }

    // MARK: - Phase C: checkpoints + handoffs

    func createCheckpoint(repoRoot: String, conversationID: String, sourceAgent: String,
                          summary: String, resumeCommand: String? = nil,
                          metadataJSON: String? = nil) async throws -> Checkpoint {
        try await nativeConversations.createCheckpoint(
            repoRoot: repoRoot,
            conversationID: conversationID,
            sourceAgent: sourceAgent,
            summary: summary,
            resumeCommand: resumeCommand,
            metadataJSON: metadataJSON
        )
    }

    func createHandoff(repoRoot: String, fromAgent: String, toAgent: String,
                       goalHint: String? = nil, targetProfile: String? = nil) async throws -> HandoffPacket {
        try await nativeConversations.createHandoff(
            repoRoot: repoRoot,
            fromAgent: fromAgent,
            toAgent: toAgent,
            goalHint: goalHint,
            targetProfile: targetProfile
        )
    }

    func markHandoffConsumed(handoffID: String) async throws {
        try await nativeConversations.markHandoffConsumed(id: handoffID)
    }

    // MARK: - Phase C: rebuilds + alias merge

    func rebuildRepoWiki(repoRoot: String) async throws -> [WikiPage] {
        try await nativeConversations.rebuildWiki(repoRoot: repoRoot)
    }

    func rebuildRepoEmbeddings(repoRoot: String) async throws -> [String: Any] {
        let result = try await nativeConversations.rebuildSearchIndex(repoRoot: repoRoot)
        return [
            "document_count": result.documentCount,
            "embedding_count": result.embeddingCount,
            "model": "native-token-hash-v1",
        ]
    }

    func mergeRepoAlias(repoRoot: String, aliasRoot: String) async throws -> [String: Any] {
        let result = try await nativeConversations.mergeRepoAlias(
            repoRoot: repoRoot,
            aliasRoot: aliasRoot
        )
        return [
            "repo_id": result.repoID,
            "repo_root": result.repoRoot,
            "alias_root": result.aliasRoot,
        ]
    }

    func importAllLocalHistory() async throws -> [String: Any] {
        let report = await nativeHistory.importAll()
        return [
            "imported": report.imported,
            "imported_count": report.total,
            "warnings": report.warnings,
        ]
    }

    /// Import a ChatMem `chatmem.db` into AI Memory's independent DB.
    /// The source file is read-only (the Rust side uses `std::fs::copy`);
    /// a timestamped backup of the existing AI Memory DB is returned.
    func importFromChatMem(sourceDBPath: String) async throws -> [String: Any] {
        let result = try await Task.detached(priority: .userInitiated) {
            try await NativeDatabaseImporter.importChatMem(
                source: URL(fileURLWithPath: sourceDBPath)
            )
        }.value
        return result.dictionary
    }

    func scanRepoConversations(repoRoot: String) async throws -> RepoHealth {
        _ = await nativeHistory.scan(repoRoot: repoRoot)
        return try await nativeConversations.repoHealth(repoRoot: repoRoot)
    }

    func loadAppSettings() async throws -> [String: Any]? {
        let preferences = try await nativeSettings.load()
        let data = try JSONEncoder().encode(preferences)
        return try JSONSerialization.jsonObject(with: data) as? [String: Any]
    }

    /// Returns the independent AI Memory data paths (db, settings, support dir).
    func appPaths() async throws -> [String: String] {
        [
            "support_dir": DataPaths.supportDir.path,
            "database": DataPaths.dbURL.path,
            "settings": DataPaths.settingsURL.path,
            "trash_dir": DataPaths.trashDir.path,
            "cache_dir": DataPaths.cacheDir.path,
        ]
    }
}
