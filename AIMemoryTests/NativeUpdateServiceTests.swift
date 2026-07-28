import XCTest
@testable import AIMemory

final class NativeUpdateServiceTests: XCTestCase {
    func testDecodesGitHubReleaseAndChoosesDMG() throws {
        let data = Data(
            """
            {
              "tag_name": "v1.2.3",
              "name": "AI Memory 1.2.3",
              "body": "Release notes",
              "html_url": "https://example.com/releases/1.2.3",
              "assets": [
                {
                  "name": "AI-Memory.zip",
                  "browser_download_url": "https://example.com/AI-Memory.zip"
                },
                {
                  "name": "AI-Memory.dmg",
                  "browser_download_url": "https://example.com/AI-Memory.dmg"
                }
              ]
            }
            """.utf8
        )

        let release = try NativeUpdateService.decodeRelease(data)
        XCTAssertEqual(release.version, "1.2.3")
        XCTAssertEqual(release.assetName, "AI-Memory.dmg")
        XCTAssertEqual(release.assetURL?.absoluteString, "https://example.com/AI-Memory.dmg")
    }

    func testSemanticVersionComparison() {
        XCTAssertTrue(NativeUpdateService.isVersion("1.10.0", newerThan: "1.9.9"))
        XCTAssertTrue(NativeUpdateService.isVersion("v2.0", newerThan: "1.99.99"))
        XCTAssertFalse(NativeUpdateService.isVersion("1.2.0", newerThan: "1.2"))
        XCTAssertFalse(NativeUpdateService.isVersion("1.1.9", newerThan: "1.2.0"))
    }

    func testDoesNotOfferAnotherProductsInstaller() throws {
        let data = Data(
            """
            {
              "tag_name": "v9.9.9",
              "name": "ChatMem 9.9.9",
              "html_url": "https://example.com/releases/9.9.9",
              "assets": [
                {
                  "name": "ChatMem-9.9.9.dmg",
                  "browser_download_url": "https://example.com/ChatMem.dmg"
                }
              ]
            }
            """.utf8
        )
        let release = try NativeUpdateService.decodeRelease(data)
        XCTAssertNil(release.assetName)
        XCTAssertNil(release.assetURL)
    }
}
