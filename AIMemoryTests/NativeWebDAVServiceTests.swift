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

    private func conversation(id: String, agent: String) -> ConversationDetail {
        ConversationDetail(
            id: id,
            sourceAgent: agent,
            projectDir: "/tmp/webdav-project",
            createdAt: "2026-07-23T10:00:00Z",
            updatedAt: "2026-07-23T11:00:00Z",
            summary: "\(agent) conversation",
            storagePath: nil,
            resumeCommand: nil,
            messages: [
                ConversationMessage(
                    id: "\(id)-message",
                    timestamp: "2026-07-23T11:00:00Z",
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
