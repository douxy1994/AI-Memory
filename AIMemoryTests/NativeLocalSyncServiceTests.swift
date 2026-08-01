// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
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
        let canonicalLocalFile = syncURL
            .appendingPathComponent("conversations/codex", isDirectory: true)
            .appendingPathComponent(
                NativeLocalSyncService.canonicalFilename("local-1") + ".json"
            )
        XCTAssertTrue(FileManager.default.fileExists(atPath: canonicalLocalFile.path))
        XCTAssertFalse(FileManager.default.fileExists(
            atPath: syncURL
                .appendingPathComponent("AIMemorySync/conversations/codex", isDirectory: true)
                .appendingPathComponent(
                    NativeLocalSyncService.canonicalFilename("local-1") + ".json"
                ).path
        ))

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
        try JSONEncoder().encode(changedRemote).write(
            to: canonicalLocalFile,
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
        try JSONEncoder().encode(remote).write(
            to: remoteFolder.appendingPathComponent(
                NativeLocalSyncService.canonicalFilename("remote-1") + ".json"
            ),
            options: [.atomic]
        )

        let fourth = try await service.sync(folder: syncURL.path)
        XCTAssertEqual(fourth.downloaded, 1)
        let imported = try await store.listConversations(agent: "claude")
        XCTAssertEqual(imported.map(\.id), ["remote-1"])
    }

    func testLegacyWindowsLayoutIsScannedWithoutManifestAndSkipsOnSecondSync() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("NativeLocalSyncServiceTests-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: root) }
        let databaseURL = root.appendingPathComponent("aimemory.db")
        let syncURL = root.appendingPathComponent("shared")
        var database: NativeDatabase? = try NativeDatabase(url: databaseURL)
        _ = try await database?.currentSchemaVersion()
        database = nil

        let id = "legacy-windows-1"
        let legacyFolder = syncURL.appendingPathComponent(
            "AIMemorySync/conversations/codex",
            isDirectory: true
        )
        try FileManager.default.createDirectory(
            at: legacyFolder,
            withIntermediateDirectories: true
        )
        let payload = windowsStylePayload(id: id)
        let legacyFile = legacyFolder.appendingPathComponent(
            NativeLocalSyncService.canonicalFilename(id) + ".json"
        )
        try payload.write(to: legacyFile, options: [.atomic])

        let store = NativeConversationStore(databaseURL: databaseURL)
        let service = NativeLocalSyncService(store: store)
        let first = try await service.sync(folder: syncURL.path)
        XCTAssertEqual(first.downloaded, 1)
        XCTAssertEqual(first.uploaded, 0)
        let imported = try await store.listConversations(agent: "codex")
        XCTAssertEqual(imported.map(\.id), [id])
        XCTAssertTrue(FileManager.default.fileExists(
            atPath: syncURL.appendingPathComponent("conversations").path
        ))

        let second = try await service.sync(folder: syncURL.path)
        XCTAssertEqual(second.uploaded, 0)
        XCTAssertEqual(second.downloaded, 0)
        XCTAssertEqual(second.skipped, 1)
        let status = try await service.status(folder: syncURL.path)
        XCTAssertEqual(status.remoteConversationCount, 1)
    }

    func testWindowsSerializerShapeSkipsWithoutRewritingCanonicalPayload() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("NativeLocalSyncServiceTests-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: root) }
        let databaseURL = root.appendingPathComponent("aimemory.db")
        let syncURL = root.appendingPathComponent("shared")
        var database: NativeDatabase? = try NativeDatabase(url: databaseURL)
        _ = try await database?.currentSchemaVersion()
        database = nil
        let store = NativeConversationStore(databaseURL: databaseURL)
        let id = "windows-shape-1"
        try await store.upsertConversation(interoperableConversation(id: id))

        let remoteFolder = syncURL.appendingPathComponent(
            "conversations/codex",
            isDirectory: true
        )
        try FileManager.default.createDirectory(
            at: remoteFolder,
            withIntermediateDirectories: true
        )
        let remoteFile = remoteFolder.appendingPathComponent(
            NativeLocalSyncService.canonicalFilename(id) + ".json"
        )
        let original = windowsStylePayload(id: id)
        try original.write(to: remoteFile, options: [.atomic])

        let result = try await NativeLocalSyncService(store: store)
            .sync(folder: syncURL.path)
        XCTAssertEqual(result.uploaded, 0)
        XCTAssertEqual(result.downloaded, 0)
        XCTAssertEqual(result.skipped, 1)
        XCTAssertEqual(try Data(contentsOf: remoteFile), original)
    }

    func testSemanticHashMatchesCrossPlatformFixture() {
        XCTAssertEqual(
            NativeLocalSyncService.semanticHash(semanticFixture()),
            "2e7f520d598623953fcf41fb1ab39b49b1644e1a3401efeb7271699b7807ff16"
        )
    }

    func testRemoteScanIgnoresManifestPathsAndAgentDirectoryMismatch() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("NativeLocalSyncServiceTests-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: root) }
        let databaseURL = root.appendingPathComponent("aimemory.db")
        let syncURL = root.appendingPathComponent("shared")
        var database: NativeDatabase? = try NativeDatabase(url: databaseURL)
        _ = try await database?.currentSchemaVersion()
        database = nil

        let external = root.appendingPathComponent("outside.json")
        let externalPayload = windowsStylePayload(id: "outside")
        try externalPayload.write(to: external, options: [.atomic])
        try FileManager.default.createDirectory(
            at: syncURL.appendingPathComponent("conversations/codex"),
            withIntermediateDirectories: true
        )
        try windowsStylePayload(id: "mismatched", agent: "claude").write(
            to: syncURL
                .appendingPathComponent("conversations/codex")
                .appendingPathComponent(
                    NativeLocalSyncService.canonicalFilename("mismatched") + ".json"
                ),
            options: [.atomic]
        )
        let manifest = """
        {"schema_version":2,"conversations":[
          {"agent":"codex","id":"outside","file":"../outside.json"}
        ]}
        """
        try Data(manifest.utf8).write(
            to: syncURL.appendingPathComponent("manifest.json"),
            options: [.atomic]
        )

        let store = NativeConversationStore(databaseURL: databaseURL)
        let result = try await NativeLocalSyncService(store: store)
            .sync(folder: syncURL.path)
        XCTAssertEqual(result.downloaded, 0)
        let ignored = try await store.listConversations(agent: "codex")
        XCTAssertEqual(ignored, [])
        XCTAssertEqual(try Data(contentsOf: external), externalPayload)
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

    private func interoperableConversation(id: String) -> ConversationDetail {
        ConversationDetail(
            id: id,
            sourceAgent: "codex",
            projectDir: "/tmp/sync-project",
            createdAt: "2026-07-23T10:00:00Z",
            updatedAt: "2026-07-23T11:00:00Z",
            summary: "codex conversation",
            storagePath: nil,
            resumeCommand: "codex resume \(id)",
            messages: [
                ConversationMessage(
                    id: "\(id)-message",
                    timestamp: "2026-07-23T11:00:00Z",
                    role: "user",
                    content: "hello",
                    toolCalls: [
                        ToolCall(
                            id: "\(id)-tool",
                            name: "read_file",
                            input: .object([
                                "answer": .number(42),
                                "path": .string("README.md"),
                            ]),
                            output: nil,
                            status: "success"
                        ),
                    ],
                    metadata: nil
                ),
            ],
            fileChanges: []
        )
    }

    private func semanticFixture() -> ConversationDetail {
        ConversationDetail(
            id: "vector-1",
            sourceAgent: "codex",
            projectDir: "/tmp/semantic",
            createdAt: "2026-07-23T10:00:00Z",
            updatedAt: "2026-07-23T11:00:00Z",
            summary: "跨平台",
            storagePath: nil,
            resumeCommand: "ignored resume",
            messages: [
                ConversationMessage(
                    id: "m-1",
                    timestamp: "2026-07-23T10:30:00Z",
                    role: "user",
                    content: "hello 🌿",
                    toolCalls: [
                        ToolCall(
                            id: "tool-1",
                            name: "shell",
                            input: .object([
                                "z": .object([
                                    "nested": .array([.bool(true), .null, .string("x")]),
                                ]),
                                "alpha": .number(1),
                            ]),
                            output: nil,
                            status: "completed"
                        ),
                    ],
                    metadata: ["ignored": .string("metadata")]
                ),
            ],
            fileChanges: [
                FileChange(
                    path: "/tmp/a.swift",
                    changeType: "modified",
                    timestamp: "2026-07-23T10:31:00Z",
                    messageId: "m-1"
                ),
            ]
        )
    }

    private func windowsStylePayload(
        id: String,
        agent: String = "codex"
    ) -> Data {
        Data(
            """
            {
              "id": "\(id)",
              "source_agent": "\(agent)",
              "project_dir": "/tmp/sync-project",
              "created_at": "2026-07-23T10:00:00Z",
              "updated_at": "2026-07-23T11:00:00Z",
              "summary": "codex conversation",
              "storage_path": null,
              "resume_command": null,
              "messages": [
                {
                  "id": "\(id)-message",
                  "timestamp": "2026-07-23T11:00:00Z",
                  "role": "user",
                  "content": "hello",
                  "tool_calls": [
                    {
                      "id": "\(id)-tool",
                      "name": "read_file",
                      "input": {"path":"README.md","answer":42},
                      "output": null,
                      "status": "success"
                    }
                  ],
                  "metadata": {}
                }
              ],
              "file_changes": []
            }
            """.utf8
        )
    }
}
