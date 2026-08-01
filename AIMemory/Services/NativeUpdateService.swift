// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
import AppKit
import Foundation

struct NativeUpdateRelease: Sendable, Equatable {
    let version: String
    let title: String
    let notes: String
    let pageURL: URL
    let assetURL: URL?
    let assetName: String?
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
    private let fileManager: FileManager

    init(
        session: URLSession = .shared,
        fileManager: FileManager = .default
    ) {
        self.session = session
        self.fileManager = fileManager
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

    func downloadAndOpen(_ release: NativeUpdateRelease) async throws -> URL {
        guard let assetURL = release.assetURL,
              let assetName = release.assetName else {
            throw NativeUpdateError.noInstallAsset
        }
        let (temporary, response) = try await session.download(from: assetURL)
        if let http = response as? HTTPURLResponse,
           !(200...299).contains(http.statusCode) {
            throw NativeUpdateError.invalidResponse(http.statusCode)
        }
        let root = FileManager.default.urls(
            for: .cachesDirectory,
            in: .userDomainMask
        )[0].appendingPathComponent("com.aimemory.app/Updates", isDirectory: true)
        try fileManager.createDirectory(at: root, withIntermediateDirectories: true)
        let safeName = URL(fileURLWithPath: assetName).lastPathComponent
        let stem = URL(fileURLWithPath: safeName)
            .deletingPathExtension().lastPathComponent
        let ext = URL(fileURLWithPath: safeName).pathExtension
        let destination = root.appendingPathComponent(
            "\(stem)-\(Int(Date().timeIntervalSince1970)).\(ext)"
        )
        try fileManager.moveItem(at: temporary, to: destination)
        await MainActor.run {
            NSWorkspace.shared.open(destination)
        }
        return destination
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
        return NativeUpdateRelease(
            version: normalizedVersion(rawVersion),
            title: (object["name"] as? String) ?? rawVersion,
            notes: (object["body"] as? String) ?? "",
            pageURL: page,
            assetURL: assetURL,
            assetName: assetName
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
