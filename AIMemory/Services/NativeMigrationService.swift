import Foundation

actor NativeMigrationService {
    private let conversations: NativeConversationStore
    private let trash: NativeTrashStore
    private let writer: NativeAgentConversationWriter
    private let importer: NativeHistoryImporter

    init(
        conversations: NativeConversationStore = NativeConversationStore(),
        trash: NativeTrashStore? = nil,
        writer: NativeAgentConversationWriter? = nil,
        importer: NativeHistoryImporter? = nil,
        home: URL = FileManager.default.homeDirectoryForCurrentUser
    ) {
        self.conversations = conversations
        self.trash = trash ?? NativeTrashStore(conversations: conversations)
        self.writer = writer ?? NativeAgentConversationWriter(home: home)
        self.importer = importer ?? NativeHistoryImporter(store: conversations, home: home)
    }

    func migrate(
        source: String,
        target: String,
        id: String,
        mode: String
    ) async throws -> NativeMigrationResult {
        guard let targetAgent = AgentKind(rawValue: target),
              NativeAgentConversationWriter.writableTargets.contains(targetAgent) else {
            throw NativeMigrationError.unsupportedTarget(target)
        }
        guard source != target else {
            throw NativeMigrationError.sameAgent
        }
        guard mode == "copy" || mode == "cut" else {
            throw NativeMigrationError.unsupportedMode(mode)
        }

        let original = try await conversations.readConversation(agent: source, id: id)
        let written: NativeAgentWriteResult
        do {
            written = try await writer.write(original, to: targetAgent)
        } catch {
            throw NativeMigrationError.targetWriteFailed(error.localizedDescription)
        }

        let importReport = await importer.importAll()
        let verified: ConversationDetail
        do {
            verified = try await conversations.readConversation(
                agent: target,
                id: written.id
            )
        } catch {
            try? await writer.discardWritten(written, target: targetAgent)
            try? await conversations.deleteIndexedConversation(agent: target, id: written.id)
            throw NativeMigrationError.targetVerificationFailed(
                importReport.warnings.joined(separator: "；")
            )
        }

        let firstSourceUser = original.messages.first {
            $0.role.lowercased() == "user"
        }?.content
        let firstTargetUser = verified.messages.first {
            $0.role.lowercased() == "user"
        }?.content
        let firstPreserved = firstSourceUser == firstTargetUser
        let countsMatch = original.messages.count == verified.messages.count
        guard countsMatch, firstPreserved else {
            try? await writer.discardWritten(written, target: targetAgent)
            try? await conversations.deleteIndexedConversation(agent: target, id: written.id)
            throw NativeMigrationError.contentVerificationFailed(
                sourceCount: original.messages.count,
                targetCount: verified.messages.count,
                firstUserPreserved: firstPreserved
            )
        }

        var cutDeleted = false
        let warnings = importReport.warnings
        if mode == "cut" {
            let trashResult = try await trash.trash(
                agent: source,
                id: id,
                retentionDays: 14,
                warnings: [
                    "目标会话已经回读验证；源 Agent 会话已移至 macOS 回收站或由源 Agent 标记归档。",
                    "AI Memory 回收站可恢复索引副本；源 Agent 原始文件需从 macOS 回收站恢复。",
                ]
            )
            do {
                try await writer.archiveSource(original)
                cutDeleted = true
            } catch {
                _ = try? await trash.restore(
                    trashID: trashResult.trashID,
                    agent: source
                )
                throw NativeMigrationError.sourceArchiveFailed(error.localizedDescription)
            }
        }

        return NativeMigrationResult(
            newID: written.id,
            source: source,
            target: target,
            mode: mode,
            verified: true,
            sourceMessageCount: original.messages.count,
            targetMessageCount: verified.messages.count,
            sourceFileCount: original.fileChanges.count,
            firstUserPreserved: firstPreserved,
            cutDeletedSource: cutDeleted,
            warnings: warnings
        )
    }
}

struct NativeMigrationResult: Sendable {
    let newID: String
    let source: String
    let target: String
    let mode: String
    let verified: Bool
    let sourceMessageCount: Int
    let targetMessageCount: Int
    let sourceFileCount: Int
    let firstUserPreserved: Bool
    let cutDeletedSource: Bool
    let warnings: [String]
}

enum NativeMigrationError: LocalizedError {
    case sameAgent
    case unsupportedTarget(String)
    case unsupportedMode(String)
    case targetWriteFailed(String)
    case targetVerificationFailed(String)
    case contentVerificationFailed(
        sourceCount: Int,
        targetCount: Int,
        firstUserPreserved: Bool
    )
    case sourceArchiveFailed(String)

    var errorDescription: String? {
        switch self {
        case .sameAgent:
            "源 Agent 与目标 Agent 不能相同。"
        case .unsupportedTarget(let target):
            "目标 Agent \(target) 不支持安全的原生会话写入。"
        case .unsupportedMode(let mode):
            "不支持的迁移模式：\(mode)"
        case .targetWriteFailed(let reason):
            "目标 Agent 写入失败，源会话未修改：\(reason)"
        case .targetVerificationFailed(let reason):
            reason.isEmpty
                ? "目标会话无法回读，已撤销目标写入，源会话未修改。"
                : "目标会话无法回读，已撤销目标写入，源会话未修改：\(reason)"
        case .contentVerificationFailed(
            let sourceCount,
            let targetCount,
            let firstUserPreserved
        ):
            "目标内容验证失败（消息 \(sourceCount)→\(targetCount)，首条用户消息\(firstUserPreserved ? "一致" : "不一致")），已撤销目标写入。"
        case .sourceArchiveFailed(let reason):
            "目标已验证，但源会话无法安全归档；AI Memory 已恢复源索引：\(reason)"
        }
    }
}
