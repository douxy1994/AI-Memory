// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
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

    // MARK: - In-place install

    /// The DMG payload, the build product and the installed copy must all be
    /// named `AIMemory.app`. Shipping `AI Memory.app` once made a drag install
    /// land beside `/Applications/AIMemory.app`, leaving the user with two apps
    /// sharing one bundle id — and the in-place installer would then never find
    /// the bundle it is supposed to replace.
    func testReleasePackagingKeepsTheInstalledBundleName() throws {
        let script = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .appendingPathComponent("script/package_macos_release.sh")
        let text = try String(contentsOf: script, encoding: .utf8)

        XCTAssertTrue(
            text.contains(#"APP="$STAGING/AIMemory.app""#),
            "打包脚本必须产出 AIMemory.app；改名会导致拖拽安装出现两个副本"
        )
        // Only effective lines matter — the comment above that assignment
        // deliberately names the old spelling to explain why it was wrong.
        let effective = text
            .split(separator: "\n", omittingEmptySubsequences: false)
            .filter { !$0.trimmingCharacters(in: .whitespaces).hasPrefix("#") }
            .joined(separator: "\n")
        XCTAssertFalse(
            effective.contains("AI Memory.app"),
            "打包脚本不得再产出带空格的 AI Memory.app"
        )
    }

    /// The relaunch helper matches the executable with awk field splitting, so a
    /// space anywhere in the bundle path would silently break the health check
    /// and the rollback that depends on it.
    func testInstalledBundlePathHasNoSpaces() {
        XCTAssertFalse("/Applications/AIMemory.app".contains(" "))
    }

    func testParseMountPointReadsHdiutilAttachOutput() {
        let output = """
        /dev/disk4          \tGUID_partition_scheme\t
        /dev/disk4s1        \tApple_APFS\t
        /dev/disk5s1        \tApple_APFS_ISC\t/Volumes/AI Memory
        """
        XCTAssertEqual(
            NativeUpdateInstaller.parseMountPoint(from: output),
            "/Volumes/AI Memory"
        )
        XCTAssertNil(NativeUpdateInstaller.parseMountPoint(from: "no mount here"))
    }

    /// Download progress needs the feed-reported size before the server sends
    /// Content-Length.
    func testDecodeReleaseCapturesAssetSize() throws {
        let data = Data(
            """
            {
              "tag_name": "v0.1.2",
              "name": "AI Memory 0.1.2",
              "html_url": "https://example.com/releases/0.1.2",
              "assets": [
                {
                  "name": "AI-Memory-0.1.2-macOS-universal.dmg",
                  "size": 9338880,
                  "browser_download_url": "https://example.com/AIMemory.dmg"
                }
              ]
            }
            """.utf8
        )
        let release = try NativeUpdateService.decodeRelease(data)
        XCTAssertEqual(release.assetSize, 9_338_880)
        XCTAssertEqual(release.assetName, "AI-Memory-0.1.2-macOS-universal.dmg")
    }
}
