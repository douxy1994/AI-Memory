// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
import AppKit
import Foundation

enum NativeUpdateInstallError: LocalizedError {
    case missingAsset
    case downloadFailed(String)
    case mountFailed(String)
    case appNotFoundInDMG
    case signatureMismatch(String)
    case installFailed(String)
    case busy

    var errorDescription: String? {
        switch self {
        case .missingAsset:
            "该版本没有可安装的 macOS DMG 附件。"
        case .downloadFailed(let detail):
            "下载更新失败：\(detail)"
        case .mountFailed(let detail):
            "无法挂载下载的 DMG：\(detail)"
        case .appNotFoundInDMG:
            "DMG 中没有找到 AIMemory.app。"
        case .signatureMismatch(let detail):
            "下载的 App 签名与项目固定身份不一致，已中止安装：\(detail)"
        case .installFailed(let detail):
            "安装更新失败：\(detail)"
        case .busy:
            "已有更新正在下载或安装中。"
        }
    }
}

enum NativeUpdateInstallOutcome: Sendable {
    /// /Applications/AIMemory.app was replaced; the caller should relaunch.
    /// `rollback` holds the previous bundle until the new one proves healthy.
    case installed(app: URL, rollback: URL?)
    /// Not running from a writable /Applications copy, so the DMG was opened
    /// for a manual install instead of replacing anything.
    case openedInstaller(URL)
}

/// In-app updater: downloads the release DMG, verifies that the bundle inside
/// carries the project's fixed signing identity, replaces
/// `/Applications/AIMemory.app` in place, and relaunches.
///
/// The bundle name must stay `AIMemory.app` end to end — build product, DMG
/// payload and installed copy. Shipping it as `AI Memory.app` once made a drag
/// install land *beside* the existing bundle, leaving two apps with the same
/// bundle id. The name also must not contain spaces: the relaunch helper below
/// matches the executable path with awk field splitting.
final class NativeUpdateInstaller: NSObject, @unchecked Sendable {
    static let shared = NativeUpdateInstaller()

    /// Mirrors EXPECTED_REQUIREMENT in script/package_macos_release.sh. Only
    /// builds signed by the project identity are ever installed.
    private let expectedRequirement =
        #"identifier "com.aimemory.app" and certificate leaf = H"a493ef6f181ec595f5216b01a4e2008778c4a592""#
    private let applicationsURL = URL(fileURLWithPath: "/Applications/AIMemory.app")

    private let lock = NSLock()
    private var busy = false

    private var progressHandler: (@Sendable (Double) -> Void)?
    private var continuation: CheckedContinuation<URL, Error>?
    private var destinationURL: URL?
    private var expectedBytes: Int64 = 0
    private var lastReportedProgress = -1.0

    // MARK: - Download

    func download(
        _ release: NativeUpdateRelease,
        progress: @escaping @Sendable (Double) -> Void
    ) async throws -> URL {
        guard let assetURL = release.assetURL,
              let assetName = release.assetName else {
            throw NativeUpdateInstallError.missingAsset
        }
        // withLock keeps the critical section synchronous; Swift 6 rejects bare
        // lock()/unlock() in an async context.
        let alreadyBusy = lock.withLock { () -> Bool in
            if busy { return true }
            busy = true
            return false
        }
        guard !alreadyBusy else { throw NativeUpdateInstallError.busy }

        let root = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("com.aimemory.app/Updates", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let destination = root.appendingPathComponent(
            URL(fileURLWithPath: assetName).lastPathComponent
        )
        try? FileManager.default.removeItem(at: destination)

        destinationURL = destination
        expectedBytes = release.assetSize
        lastReportedProgress = -1
        progressHandler = progress

        var request = URLRequest(url: assetURL)
        request.setValue("AIMemory", forHTTPHeaderField: "User-Agent")
        let session = URLSession(
            configuration: .default,
            delegate: self,
            delegateQueue: nil
        )
        do {
            return try await withCheckedThrowingContinuation { continuation in
                self.continuation = continuation
                session.downloadTask(with: request).resume()
            }
        } catch {
            setBusy(false)
            throw error
        }
    }

    // MARK: - Install

    /// Mounts the DMG, verifies the signature, and swaps the bundle in place.
    /// Blocking; call from a background context.
    func install(from dmgURL: URL) throws -> NativeUpdateInstallOutcome {
        defer { setBusy(false) }

        let mountOutput = try run(
            "/usr/bin/hdiutil",
            ["attach", "-nobrowse", "-readonly", dmgURL.path]
        ) { NativeUpdateInstallError.mountFailed($0) }
        let mountPoint = Self.parseMountPoint(from: mountOutput)
        // Registered right after attach so a parse failure still detaches.
        defer {
            if let mountPoint {
                _ = try? run(
                    "/usr/bin/hdiutil",
                    ["detach", "-quiet", "-force", mountPoint]
                ) { NativeUpdateInstallError.mountFailed($0) }
            }
        }
        guard let mountPoint else {
            throw NativeUpdateInstallError.mountFailed(
                mountOutput.trimmingCharacters(in: .whitespacesAndNewlines)
            )
        }

        let mountedApp = URL(fileURLWithPath: mountPoint)
            .appendingPathComponent("AIMemory.app")
        guard FileManager.default.fileExists(atPath: mountedApp.path) else {
            throw NativeUpdateInstallError.appNotFoundInDMG
        }
        try verifyStableIdentity(of: mountedApp)

        // Replace in place only when this very process is the /Applications
        // copy and that directory is writable; otherwise hand over the DMG.
        let runningURL = Bundle.main.bundleURL.resolvingSymlinksInPath()
        guard runningURL == applicationsURL,
              FileManager.default.isWritableFile(atPath: "/Applications")
        else {
            let dmg = dmgURL
            DispatchQueue.main.async { NSWorkspace.shared.open(dmg) }
            return .openedInstaller(dmgURL)
        }

        // Stage the new bundle, move the old one aside, then swap. The old copy
        // survives until the relaunch helper sees the new process stay alive.
        let staging = URL(fileURLWithPath: "/Applications/.AIMemory-update-\(UUID().uuidString)")
        let sidecar = URL(fileURLWithPath: "/Applications/.AIMemory-old-\(UUID().uuidString)")
        var movedOld = false
        do {
            try FileManager.default.copyItem(at: mountedApp, to: staging)
            if FileManager.default.fileExists(atPath: applicationsURL.path) {
                try FileManager.default.moveItem(at: applicationsURL, to: sidecar)
                movedOld = true
            }
            do {
                try FileManager.default.moveItem(at: staging, to: applicationsURL)
            } catch {
                if movedOld {
                    try? FileManager.default.moveItem(at: sidecar, to: applicationsURL)
                }
                try? FileManager.default.removeItem(at: staging)
                throw error
            }
        } catch {
            try? FileManager.default.removeItem(at: staging)
            throw NativeUpdateInstallError.installFailed(error.localizedDescription)
        }
        return .installed(app: applicationsURL, rollback: movedOld ? sidecar : nil)
    }

    // MARK: - Relaunch

    /// Relaunches after this process exits. The previous bundle stays beside the
    /// replacement until the new process survives a short health window; if the
    /// new build fails to start, the helper restores and reopens the old one.
    func relaunch(appURL: URL, rollbackURL: URL?) throws {
        let helper = Process()
        helper.executableURL = URL(fileURLWithPath: "/bin/sh")
        helper.arguments = [
            "-c",
            """
            while /bin/kill -0 "$1" 2>/dev/null; do /bin/sleep 0.2; done
            /usr/bin/open -n "$2"
            newpid=""
            i=0
            while [ "$i" -lt 60 ]; do
              newpid=$(/bin/ps -axo pid=,command= | /usr/bin/awk -v exe="$2/Contents/MacOS/AIMemory" '$2 == exe { print $1; exit }')
              [ -n "$newpid" ] && break
              i=$((i + 1)); /bin/sleep 0.25
            done
            if [ -n "$newpid" ]; then
              /bin/sleep 8
              if /bin/kill -0 "$newpid" 2>/dev/null; then
                [ -n "$3" ] && /bin/rm -rf "$3"
                exit 0
              fi
            fi
            if [ -n "$3" ] && [ -e "$3" ]; then
              /bin/rm -rf "$2"
              /bin/mv "$3" "$2"
              /usr/bin/open -n "$2"
            fi
            """,
            "aimemory-relaunch",
            "\(ProcessInfo.processInfo.processIdentifier)",
            appURL.path,
            rollbackURL?.path ?? "",
        ]
        helper.standardOutput = FileHandle.nullDevice
        helper.standardError = FileHandle.nullDevice
        try helper.run()
        NSApp.terminate(nil)
    }

    // MARK: - Helpers

    private func verifyStableIdentity(of app: URL) throws {
        _ = try run(
            "/usr/bin/codesign",
            ["--verify", "--deep", "--strict", app.path]
        ) { NativeUpdateInstallError.signatureMismatch($0) }
        let description = try run(
            "/usr/bin/codesign",
            ["-d", "-r-", app.path]
        ) { NativeUpdateInstallError.signatureMismatch($0) }
        guard let requirement = description
            .split(separator: "\n")
            .first(where: { $0.hasPrefix("designated => ") })
            .map({ String($0.dropFirst("designated => ".count)) }),
              requirement == expectedRequirement
        else {
            throw NativeUpdateInstallError.signatureMismatch(
                description.trimmingCharacters(in: .whitespacesAndNewlines)
            )
        }
    }

    static func parseMountPoint(from output: String) -> String? {
        for line in output.split(separator: "\n").reversed() {
            guard let last = line.split(separator: "\t").last,
                  last.hasPrefix("/Volumes") else { continue }
            return String(last)
        }
        return nil
    }

    @discardableResult
    private func run(
        _ launchPath: String,
        _ arguments: [String],
        errorMapper: (String) -> NativeUpdateInstallError
    ) throws -> String {
        let process = Process()
        let pipe = Pipe()
        process.executableURL = URL(fileURLWithPath: launchPath)
        process.arguments = arguments
        process.standardOutput = pipe
        process.standardError = pipe
        try process.run()
        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        let output = String(data: data, encoding: .utf8) ?? ""
        guard process.terminationStatus == 0 else {
            throw errorMapper(output.trimmingCharacters(in: .whitespacesAndNewlines))
        }
        return output
    }

    private func setBusy(_ value: Bool) {
        lock.withLock { busy = value }
    }

    private func finish(_ result: Result<URL, Error>) {
        guard let continuation else { return }
        self.continuation = nil
        progressHandler = nil
        destinationURL = nil
        continuation.resume(with: result)
    }
}

extension NativeUpdateInstaller: URLSessionDownloadDelegate {
    func urlSession(
        _ session: URLSession,
        downloadTask: URLSessionDownloadTask,
        didWriteData bytesWritten: Int64,
        totalBytesWritten: Int64,
        totalBytesExpectedToWrite: Int64
    ) {
        let expected = totalBytesExpectedToWrite > 0
            ? totalBytesExpectedToWrite
            : expectedBytes
        guard expected > 0 else { return }
        let fraction = min(1, Double(totalBytesWritten) / Double(expected))
        // Throttle to whole-percent steps so a fast link cannot flood the UI.
        guard fraction - lastReportedProgress >= 0.01 || fraction >= 1 else { return }
        lastReportedProgress = fraction
        progressHandler?(fraction)
    }

    func urlSession(
        _ session: URLSession,
        downloadTask: URLSessionDownloadTask,
        didFinishDownloadingTo location: URL
    ) {
        defer { session.finishTasksAndInvalidate() }
        guard let destination = destinationURL else { return }
        do {
            if let http = downloadTask.response as? HTTPURLResponse,
               !(200..<300).contains(http.statusCode) {
                throw NativeUpdateInstallError.downloadFailed("HTTP \(http.statusCode)")
            }
            try FileManager.default.moveItem(at: location, to: destination)
            finish(.success(destination))
        } catch {
            finish(.failure(error))
        }
    }

    func urlSession(
        _ session: URLSession,
        task: URLSessionTask,
        didCompleteWithError error: Error?
    ) {
        guard let error else { return }
        session.finishTasksAndInvalidate()
        finish(.failure(
            NativeUpdateInstallError.downloadFailed(error.localizedDescription)
        ))
    }
}
