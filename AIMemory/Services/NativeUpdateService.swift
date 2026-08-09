// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
import Foundation

struct NativeUpdateRelease: Sendable, Equatable {
    let version: String
    let title: String
    let notes: String
    let pageURL: URL
    let assetURL: URL?
    let assetName: String?
    /// Byte size reported by the feed; 0 when unknown. Used to show download
    /// progress before the server sends Content-Length.
    var assetSize: Int64 = 0
}

enum NativeUpdateCheckResult: Sendable, Equatable {
    case current(NativeUpdateRelease)
    case available(NativeUpdateRelease)
}

enum NativeUpdateError: LocalizedError {
    case unconfigured
    case invalidFeed
    case invalidResponse(Int)
    case noInstallAsset

    var errorDescription: String? {
        switch self {
        case .unconfigured:
            "尚未配置 AI Memory 更新源。"
        case .invalidFeed:
            "更新源返回了无法识别的版本信息。"
        case .invalidResponse(let status):
            "更新服务器返回 HTTP \(status)。"
        case .noInstallAsset:
            "该版本没有可安装的 macOS DMG、PKG 或 ZIP。"
        }
    }
}

/// Native GitHub-compatible update checker. No third-party updater framework
/// is embedded; release metadata and install assets are handled by URLSession.
actor NativeUpdateService {
    private let session: URLSession

    init(session: URLSession = .shared) {
        self.session = session
    }

    func check(
        feedURL: URL?,
        currentVersion: String
    ) async throws -> NativeUpdateCheckResult {
        guard let feedURL else { throw NativeUpdateError.unconfigured }
        let (data, response) = try await session.data(from: feedURL)
        if let http = response as? HTTPURLResponse,
           !(200...299).contains(http.statusCode) {
            throw NativeUpdateError.invalidResponse(http.statusCode)
        }
        let release = try Self.decodeRelease(data)
        return Self.isVersion(release.version, newerThan: currentVersion)
            ? .available(release)
            : .current(release)
    }

    static func decodeRelease(_ data: Data) throws -> NativeUpdateRelease {
        guard let object = try JSONSerialization.jsonObject(with: data)
            as? [String: Any],
              let rawVersion = object["tag_name"] as? String,
              !rawVersion.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
              let page = (object["html_url"] as? String).flatMap(URL.init(string:))
        else {
            throw NativeUpdateError.invalidFeed
        }
        let assets = (object["assets"] as? [[String: Any]] ?? []).filter {
            asset in
            guard let name = (asset["name"] as? String)?.lowercased() else {
                return false
            }
            let compact = name.replacingOccurrences(
                of: #"[^a-z0-9]"#,
                with: "",
                options: .regularExpression
            )
            return compact.contains("aimemory")
        }
        let preferred = assets.first { asset in
            guard let name = (asset["name"] as? String)?.lowercased() else {
                return false
            }
            return name.hasSuffix(".dmg") || name.hasSuffix(".pkg")
        } ?? assets.first { asset in
            ((asset["name"] as? String)?.lowercased().hasSuffix(".zip")) == true
        }
        let assetName = preferred?["name"] as? String
        let assetURL = (preferred?["browser_download_url"] as? String)
            .flatMap(URL.init(string:))
        let assetSize = (preferred?["size"] as? NSNumber)?.int64Value ?? 0
        return NativeUpdateRelease(
            version: normalizedVersion(rawVersion),
            title: (object["name"] as? String) ?? rawVersion,
            notes: (object["body"] as? String) ?? "",
            pageURL: page,
            assetURL: assetURL,
            assetName: assetName,
            assetSize: assetSize
        )
    }

    static func isVersion(_ candidate: String, newerThan current: String) -> Bool {
        let lhs = versionComponents(candidate)
        let rhs = versionComponents(current)
        let count = max(lhs.count, rhs.count)
        for index in 0..<count {
            let left = index < lhs.count ? lhs[index] : 0
            let right = index < rhs.count ? rhs[index] : 0
            if left != right { return left > right }
        }
        return false
    }

    private static func normalizedVersion(_ value: String) -> String {
        value.trimmingCharacters(in: .whitespacesAndNewlines)
            .replacingOccurrences(
                of: #"^[vV]"#,
                with: "",
                options: .regularExpression
            )
    }

    private static func versionComponents(_ value: String) -> [Int] {
        normalizedVersion(value)
            .split(separator: ".", omittingEmptySubsequences: false)
            .map { component in
                Int(component.prefix(while: \.isNumber)) ?? 0
            }
    }
}
