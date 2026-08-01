// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
import XCTest
@testable import AIMemory

final class NativeWebDAVServiceTests: XCTestCase {
    override func tearDown() {
        WebDAVURLProtocol.handler = nil
        super.tearDown()
    }

    func testVerifyUsesPropfindAndBasicAuthentication() async throws {
        let expectation = expectation(description: "request")
        WebDAVURLProtocol.handler = { request in
            XCTAssertEqual(request.httpMethod, "PROPFIND")
            XCTAssertEqual(request.value(forHTTPHeaderField: "Depth"), "0")
            XCTAssertEqual(
                request.value(forHTTPHeaderField: "Authorization"),
                "Basic " + Data("alice:secret".utf8).base64EncodedString()
            )
            expectation.fulfill()
            return (207, Data())
        }
        let service = NativeWebDAVService(
            session: makeSession()
        )
        let result = try await service.verify(
            scheme: "http",
            host: "dav.example.test",
            path: "/webdav",
            remotePath: "chatmem",
            username: "alice",
            password: "secret"
        )
        await fulfillment(of: [expectation], timeout: 1)
        XCTAssertEqual(result.status, 207)
        XCTAssertEqual(result.url, "http://dav.example.test/webdav/chatmem/")
    }

    func testEmptyDatabaseSyncStillUploadsVersionedManifest() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("NativeWebDAVServiceTests-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: root) }
        let databaseURL = root.appendingPathComponent("aimemory.db")
        var database: NativeDatabase? = try NativeDatabase(url: databaseURL)
        _ = try await database?.currentSchemaVersion()
        database = nil

        let manifest = expectation(description: "manifest")
        WebDAVURLProtocol.handler = { request in
            if request.httpMethod == "GET" {
                return (404, Data())
            }
            if request.httpMethod == "PUT" {
                XCTAssertTrue(request.url?.path.hasSuffix("/manifest.json") == true)
                manifest.fulfill()
                return (201, Data())
            }
            return (207, Data())
        }
        let service = NativeWebDAVService(
            conversations: NativeConversationStore(databaseURL: databaseURL),
            session: makeSession()
        )
        let progress = WebDAVProgressCollector()
        let result = try await service.sync(
            host: "dav.example.test",
            path: "chatmem",
            username: "alice",
            password: "secret",
            progress: { update in
                await progress.append(update)
            }
        )
        await fulfillment(of: [manifest], timeout: 1)
        XCTAssertEqual(result.uploadedCount, 0)
        XCTAssertEqual(result.downloadedCount, 0)
        XCTAssertEqual(result.totalCount, 0)
        XCTAssertTrue(result.manifestUploaded)
        XCTAssertTrue(result.errors.isEmpty)
        let updates = await progress.values
        XCTAssertEqual(updates.first?.completedCount, 0)
        XCTAssertEqual(updates.first?.totalCount, 0)
        XCTAssertEqual(updates.last?.uploadedCount, 0)
        XCTAssertEqual(updates.last?.completedCount, 0)
        XCTAssertEqual(updates.last?.totalCount, 0)
        XCTAssertEqual(updates.last?.uploadingManifest, true)
    }

    func testIncrementalSyncSkipsUnchangedConversationWithoutPUT() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("NativeWebDAVServiceTests-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: root) }
        let databaseURL = root.appendingPathComponent("aimemory.db")
        var database: NativeDatabase? = try NativeDatabase(url: databaseURL)
        _ = try await database?.currentSchemaVersion()
        database = nil
        let store = NativeConversationStore(databaseURL: databaseURL)
        let detail = conversation(id: "local-1", agent: "codex")
        try await store.upsertConversation(detail)

        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys, .withoutEscapingSlashes]
        // A prior sync serializes the canonical record read back from the
        // native store, not the caller's pre-insert value.
        let storedDetail = try await store.readConversation(
            agent: "codex",
            id: detail.id
        )
        let body = try encoder.encode(storedDetail)
        let manifest = try JSONSerialization.data(
            withJSONObject: [
                "schema_version": 2,
                "conversations": [[
                    "agent": "codex",
                    "id": "local-1",
                    "file": "conversations/codex/local-1.json",
                    "updated_at": detail.updatedAt,
                    "sha256": NativeWebDAVService.contentHash(body),
                ]],
            ]
        )
        var putCount = 0
        WebDAVURLProtocol.handler = { request in
            if request.httpMethod == "GET",
               request.url?.path.hasSuffix("/manifest.json") == true {
                return (200, manifest)
            }
            if request.httpMethod == "PUT" {
                putCount += 1
                return (201, Data())
            }
            return (207, Data())
        }

        let result = try await NativeWebDAVService(
            conversations: store,
            session: makeSession()
        ).sync(
            host: "dav.example.test",
            path: "chatmem",
            username: "alice",
            password: "secret"
        )

        XCTAssertEqual(result.uploadedCount, 0)
        XCTAssertEqual(result.downloadedCount, 0)
        XCTAssertEqual(result.skippedCount, 1)
        XCTAssertFalse(result.manifestUploaded)
        XCTAssertEqual(putCount, 0)
    }

    func testSemanticDigestMatchesSharedProtocolVector() {
        let detail = ConversationDetail(
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
        let equivalentAfterStoreRoundTrip = ConversationDetail(
            id: detail.id,
            sourceAgent: detail.sourceAgent,
            projectDir: detail.projectDir,
            createdAt: detail.createdAt,
            updatedAt: detail.updatedAt,
            summary: detail.summary,
            storagePath: detail.storagePath,
            resumeCommand: "another generated command",
            messages: [
                ConversationMessage(
                    id: "m-1",
                    timestamp: "2026-07-23T10:30:00Z",
                    role: "user",
                    content: "hello 🌿",
                    toolCalls: detail.messages[0].toolCalls,
                    metadata: [:]
                ),
            ],
            fileChanges: detail.fileChanges
        )

        let expected =
            "aimemory-conversation-v1:41c37b3f58708d33d64d27c22a6f37ac74559d75b7d488b3c574f1a9f63db550"
        XCTAssertEqual(NativeWebDAVService.semanticDigest(detail), expected)
        XCTAssertEqual(NativeWebDAVService.semanticDigest(equivalentAfterStoreRoundTrip), expected)
    }

    func testIncrementalSyncSkipsSemanticEquivalentSerializerVariant() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("NativeWebDAVServiceTests-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: root) }
        let databaseURL = root.appendingPathComponent("aimemory.db")
        var database: NativeDatabase? = try NativeDatabase(url: databaseURL)
        _ = try await database?.currentSchemaVersion()
        database = nil
        let store = NativeConversationStore(databaseURL: databaseURL)
        let detail = conversation(id: "semantic", agent: "codex")
        try await store.upsertConversation(detail)
        let storedDetail = try await store.readConversation(agent: "codex", id: detail.id)
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys, .withoutEscapingSlashes]
        let localBytes = try encoder.encode(storedDetail)
        let remoteBody = try JSONSerialization.data(
            withJSONObject: JSONSerialization.jsonObject(with: localBytes),
            options: [.prettyPrinted]
        )
        XCTAssertNotEqual(localBytes, remoteBody)
        let filename = Data(storedDetail.id.utf8).base64EncodedString()
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "") + ".json"
        let manifest = try JSONSerialization.data(
            withJSONObject: [
                "schema_version": 2,
                "conversations": [[
                    "agent": storedDetail.sourceAgent,
                    "id": storedDetail.id,
                    "file": "conversations/\(storedDetail.sourceAgent)/\(filename)",
                    "updated_at": storedDetail.updatedAt,
                    "sha256": "different-serializer-byte-hash",
                    "semantic_digest": NativeWebDAVService.semanticDigest(storedDetail),
                ]],
            ]
        )
        var conversationPutCount = 0
        var manifestPutCount = 0
        WebDAVURLProtocol.handler = { request in
            if request.httpMethod == "GET",
               request.url?.path.hasSuffix("/manifest.json") == true {
                return (200, manifest)
            }
            if request.httpMethod == "GET" {
                return (200, remoteBody)
            }
            if request.httpMethod == "PUT" {
                if request.url?.path.hasSuffix("/manifest.json") == true {
                    manifestPutCount += 1
                } else {
                    conversationPutCount += 1
                }
                return (201, Data())
            }
            return (207, Data())
        }

        let result = try await NativeWebDAVService(
            conversations: store,
            session: makeSession()
        ).sync(
            host: "dav.example.test",
            path: "chatmem",
            username: "alice",
            password: "secret"
        )

        XCTAssertEqual(result.uploadedCount, 0)
        XCTAssertEqual(result.skippedCount, 1)
        XCTAssertFalse(result.manifestUploaded)
        XCTAssertEqual(conversationPutCount, 0)
        XCTAssertEqual(manifestPutCount, 0)
    }

    func testIncrementalSyncLegacyEqualTimestampDifferentContentUploadsLocal() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("NativeWebDAVServiceTests-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: root) }
        let databaseURL = root.appendingPathComponent("aimemory.db")
        var database: NativeDatabase? = try NativeDatabase(url: databaseURL)
        _ = try await database?.currentSchemaVersion()
        database = nil
        let store = NativeConversationStore(databaseURL: databaseURL)
        let local = conversation(
            id: "legacy-conflict",
            agent: "codex",
            summary: "local content",
            content: "local message"
        )
        try await store.upsertConversation(local)
        let storedLocal = try await store.readConversation(agent: "codex", id: local.id)
        let remote = conversation(
            id: local.id,
            agent: local.sourceAgent,
            summary: "remote content",
            content: "remote message"
        )
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys, .withoutEscapingSlashes]
        let remoteBody = try encoder.encode(remote)
        let filename = Data(local.id.utf8).base64EncodedString()
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "") + ".json"
        let manifest = try JSONSerialization.data(
            withJSONObject: [
                "schema_version": 1,
                "conversations": [[
                    "agent": local.sourceAgent,
                    "id": local.id,
                    "file": "conversations/\(local.sourceAgent)/\(filename)",
                    "updated_at": storedLocal.updatedAt,
                ]],
            ]
        )
        var conversationPutCount = 0
        var manifestPutCount = 0
        WebDAVURLProtocol.handler = { request in
            if request.httpMethod == "GET",
               request.url?.path.hasSuffix("/manifest.json") == true {
                return (200, manifest)
            }
            if request.httpMethod == "GET" {
                return (200, remoteBody)
            }
            if request.httpMethod == "PUT" {
                if request.url?.path.hasSuffix("/manifest.json") == true {
                    manifestPutCount += 1
                } else {
                    conversationPutCount += 1
                }
                return (201, Data())
            }
            return (207, Data())
        }

        let result = try await NativeWebDAVService(
            conversations: store,
            session: makeSession()
        ).sync(
            host: "dav.example.test",
            path: "chatmem",
            username: "alice",
            password: "secret"
        )

        XCTAssertEqual(result.uploadedCount, 1)
        XCTAssertEqual(result.skippedCount, 0)
        XCTAssertEqual(conversationPutCount, 1)
        XCTAssertEqual(manifestPutCount, 1)
    }

    func testIncrementalSyncDownloadsRemoteOnlyConversation() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("NativeWebDAVServiceTests-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: root) }
        let databaseURL = root.appendingPathComponent("aimemory.db")
        var database: NativeDatabase? = try NativeDatabase(url: databaseURL)
        _ = try await database?.currentSchemaVersion()
        database = nil
        let store = NativeConversationStore(databaseURL: databaseURL)
        let detail = conversation(id: "remote-1", agent: "claude")
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys, .withoutEscapingSlashes]
        let body = try encoder.encode(detail)
        let manifest = try JSONSerialization.data(
            withJSONObject: [
                "schema_version": 2,
                "conversations": [[
                    "agent": "claude",
                    "id": "remote-1",
                    "file": "conversations/claude/remote-1.json",
                    "updated_at": detail.updatedAt,
                    "sha256": NativeWebDAVService.contentHash(body),
                ]],
            ]
        )
        WebDAVURLProtocol.handler = { request in
            if request.httpMethod == "GET",
               request.url?.path.hasSuffix("/manifest.json") == true {
                return (200, manifest)
            }
            if request.httpMethod == "GET" {
                return (200, body)
            }
            return (207, Data())
        }

        let result = try await NativeWebDAVService(
            conversations: store,
            session: makeSession()
        ).sync(
            host: "dav.example.test",
            path: "chatmem",
            username: "alice",
            password: "secret"
        )

        XCTAssertEqual(result.downloadedCount, 1)
        XCTAssertEqual(result.uploadedCount, 0)
        let downloadedIDs = try await store.listConversations(
            agent: "claude"
        ).map(\.id)
        XCTAssertEqual(downloadedIDs, ["remote-1"])
    }

    private func makeSession() -> URLSession {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [WebDAVURLProtocol.self]
        return URLSession(configuration: configuration)
    }

    private func conversation(
        id: String,
        agent: String,
        summary: String? = nil,
        content: String = "hello"
    ) -> ConversationDetail {
        ConversationDetail(
            id: id,
            sourceAgent: agent,
            projectDir: "/tmp/webdav-project",
            createdAt: "2026-07-23T10:00:00Z",
            updatedAt: "2026-07-23T11:00:00Z",
            summary: summary ?? "\(agent) conversation",
            storagePath: nil,
            resumeCommand: nil,
            messages: [
                ConversationMessage(
                    id: "\(id)-message",
                    timestamp: "2026-07-23T11:00:00Z",
                    role: "user",
                    content: content,
                    toolCalls: [],
                    metadata: [:]
                ),
            ],
            fileChanges: []
        )
    }
}

private actor WebDAVProgressCollector {
    private(set) var values: [NativeWebDAVSyncProgress] = []

    func append(_ value: NativeWebDAVSyncProgress) {
        values.append(value)
    }
}

private final class WebDAVURLProtocol: URLProtocol, @unchecked Sendable {
    nonisolated(unsafe) static var handler: ((URLRequest) throws -> (Int, Data))?

    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
        do {
            guard let handler = Self.handler else {
                throw URLError(.unsupportedURL)
            }
            let (status, body) = try handler(request)
            let response = HTTPURLResponse(
                url: request.url!,
                statusCode: status,
                httpVersion: "HTTP/1.1",
                headerFields: [:]
            )!
            client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
            client?.urlProtocol(self, didLoad: body)
            client?.urlProtocolDidFinishLoading(self)
        } catch {
            client?.urlProtocol(self, didFailWithError: error)
        }
    }

    override func stopLoading() {}
}
