import XCTest
@testable import AIMemory

final class NativeLocalSyncServiceTests: XCTestCase {
    func testBidirectionalSyncUploadsSkipsAndImportsRemoteConversation() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("NativeLocalSyncServiceTests-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: root) }
        let databaseURL = root.appendingPathComponent("aimemory.db")
        let syncURL = root.appendingPathComponent("sync")
        var database: NativeDatabase? = try NativeDatabase(url: databaseURL)
        _ = try await database?.currentSchemaVersion()
        database = nil
        let store = NativeConversationStore(databaseURL: databaseURL)
        try await store.upsertConversation(conversation(id: "local-1", agent: "codex"))

        let service = NativeLocalSyncService(store: store)
        let first = try await service.sync(folder: syncURL.path)
        XCTAssertEqual(first.uploaded, 1)
        XCTAssertEqual(first.downloaded, 0)
        let second = try await service.sync(folder: syncURL.path)
        XCTAssertEqual(second.skipped, 1)

        var changedRemote = conversation(id: "local-1", agent: "codex")
        changedRemote = ConversationDetail(
            id: changedRemote.id,
            sourceAgent: changedRemote.sourceAgent,
            projectDir: changedRemote.projectDir,
            createdAt: changedRemote.createdAt,
            updatedAt: changedRemote.updatedAt,
            summary: changedRemote.summary,
            storagePath: changedRemote.storagePath,
            resumeCommand: changedRemote.resumeCommand,
            messages: [
                ConversationMessage(
                    id: "local-1-message",
                    timestamp: changedRemote.updatedAt,
                    role: "user",
                    content: "changed with the same timestamp",
                    toolCalls: [],
                    metadata: [:]
                ),
            ],
            fileChanges: []
        )
        let localRemoteFile = syncURL
            .appendingPathComponent("conversations/codex", isDirectory: true)
            .appendingPathComponent(
                NativeLocalSyncService.idToFilename("local-1") + ".json"
            )
        try JSONEncoder().encode(changedRemote).write(
            to: localRemoteFile,
            options: [.atomic]
        )
        let contentConflict = try await service.sync(folder: syncURL.path)
        XCTAssertEqual(contentConflict.uploaded, 1)
        XCTAssertEqual(contentConflict.conflictsResolved, 1)

        let remote = conversation(
            id: "remote-1",
            agent: "claude",
            updatedAt: "2026-07-23T12:00:00Z"
        )
        let remoteFolder = syncURL
            .appendingPathComponent("conversations/claude", isDirectory: true)
        try FileManager.default.createDirectory(
            at: remoteFolder,
            withIntermediateDirectories: true
        )
        let encoder = JSONEncoder()
        try encoder.encode(remote).write(
            to: remoteFolder.appendingPathComponent("remote-1.json"),
            options: [.atomic]
        )

        let fourth = try await service.sync(folder: syncURL.path)
        XCTAssertEqual(fourth.downloaded, 1)
        let imported = try await store.listConversations(agent: "claude")
        XCTAssertEqual(imported.map(\.id), ["remote-1"])
    }

    func testCloudReadinessDetectsLockFiles() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("NativeLocalSyncServiceTests-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        try Data().write(to: root.appendingPathComponent("upload.partial"))
        let service = NativeLocalSyncService(
            store: NativeConversationStore(databaseURL: root.appendingPathComponent("none.db"))
        )
        let result = await service.readiness(folder: root.path)
        XCTAssertTrue(result.hasLockFiles)
        XCTAssertEqual(result.recommendedAction, "wait")
    }

    private func conversation(
        id: String,
        agent: String,
        updatedAt: String = "2026-07-23T11:00:00Z"
    ) -> ConversationDetail {
        ConversationDetail(
            id: id,
            sourceAgent: agent,
            projectDir: "/tmp/sync-project",
            createdAt: "2026-07-23T10:00:00Z",
            updatedAt: updatedAt,
            summary: "\(agent) conversation",
            storagePath: nil,
            resumeCommand: nil,
            messages: [
                ConversationMessage(
                    id: "\(id)-message",
                    timestamp: updatedAt,
                    role: "user",
                    content: "hello",
                    toolCalls: [],
                    metadata: [:]
                ),
            ],
            fileChanges: []
        )
    }
}
