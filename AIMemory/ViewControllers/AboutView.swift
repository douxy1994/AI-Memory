// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
import AppKit
import SwiftUI

struct AboutView: View {
    @ObservedObject var store: AppStore
    @State private var settings: [String: Any] = [:]
    @State private var autoCheckUpdates = true
    @State private var updateFeedURL = ""
    @State private var githubURL: URL?
    @State private var updateStatus = "尚未检查更新"
    @State private var updateBusy = false
    @State private var availableRelease: NativeUpdateRelease?
    @State private var readinessReport: UpgradeReadinessReport?
    @State private var readinessBusy = false
    private let updateService = NativeUpdateService()

    var body: some View {
        ScrollView {
            VStack(spacing: 22) {
                hero
                actionBar
                updateStatusLine
                updateAndDiagnostics
                releaseNotes
                productDescription
                footer
            }
            .padding(.horizontal, 42)
            .padding(.top, 42)
            .padding(.bottom, 30)
            .frame(maxWidth: .infinity)
        }
        .frame(width: 590, height: 700)
        .background(.thickMaterial)
        .task { await loadConfiguration() }
        .task(id: store.aboutUpdateCheckRequest) {
            guard store.aboutUpdateCheckRequest > 0 else { return }
            await loadConfiguration()
            await checkForUpdates(automaticInstall: true)
        }
    }

    private var hero: some View {
        VStack(spacing: 13) {
            Image(nsImage: AppBrandIcon.image)
                .resizable()
                .interpolation(.high)
                .frame(width: 96, height: 96)
                .clipShape(
                    RoundedRectangle(cornerRadius: 22, style: .continuous)
                )
                .shadow(color: Theme.accent.opacity(0.24), radius: 18, y: 8)
                .accessibilityLabel("AI Memory Logo")

            Text("AI Memory")
                .font(Theme.appFont(size: 30, weight: .bold, design: .rounded))

            HStack(spacing: 8) {
                versionPill("正式版本 \(marketingVersion)")
                versionPill("开发版本 \(buildVersion)")
            }

            Text("面向 AI Agent 与 CLI 的本地优先记忆、历史检索和工作接续工具。")
                .font(Theme.appFont(size: 13))
                .foregroundStyle(Theme.secondaryText)
                .multilineTextAlignment(.center)
                .frame(maxWidth: 430)
        }
    }

    private var actionBar: some View {
        HStack(spacing: 10) {
            Button {
                guard let githubURL else { return }
                NSWorkspace.shared.open(githubURL)
            } label: {
                HStack(spacing: 6) {
                    Image("GitHubMark")
                        .renderingMode(.template)
                        .resizable()
                        .scaledToFit()
                        .frame(width: 15, height: 15)
                    Text("GitHub")
                }
                    .frame(minWidth: 92)
            }
            .adaptiveGlassButtonStyle()
            .disabled(githubURL == nil)
            .help(
                githubURL == nil
                    ? "尚未配置项目 GitHub Releases 更新源"
                    : "打开项目 GitHub"
            )

            Button {
                Task { await checkForUpdates(automaticInstall: false) }
            } label: {
                HStack(spacing: 6) {
                    if updateBusy {
                        ProgressView().controlSize(.small)
                    } else {
                        Image(systemName: "arrow.clockwise")
                    }
                    Text(updateBusy ? "正在检查…" : "检查更新")
                }
                .frame(minWidth: 104)
            }
            .adaptiveGlassButtonStyle(prominent: true)
            .disabled(updateBusy)
        }
    }

    private var updateStatusLine: some View {
        VStack(spacing: 4) {
            Text(updateStatus)
                .font(Theme.appFont(size: 11, weight: .medium))
                .foregroundStyle(Theme.secondaryText)
                .multilineTextAlignment(.center)
            if let release = availableRelease {
                HStack(spacing: 8) {
                    if release.assetURL != nil {
                        Button("更新并重启") {
                            Task {
                                do {
                                    try await installUpdate(release)
                                } catch {
                                    updateStatus = "更新失败：\(error.localizedDescription)"
                                }
                            }
                        }
                        .adaptiveGlassButtonStyle(prominent: true)
                        .disabled(updateBusy)
                    }
                    Link("查看发布说明", destination: release.pageURL)
                }
            }
        }
    }

    private var updateAndDiagnostics: some View {
        VStack(alignment: .leading, spacing: 13) {
            Label("更新与诊断", systemImage: "checkmark.shield")
                .font(Theme.appFont(size: 15, weight: .semibold))

            Toggle("自动检查更新", isOn: autoCheckUpdatesBinding)

            HStack(spacing: 8) {
                TextField(
                    "GitHub Releases API 地址",
                    text: $updateFeedURL,
                    prompt: Text("例如：https://api.github.com/repos/owner/repo/releases/latest")
                )
                .textFieldStyle(.roundedBorder)
                Button("保存更新源") {
                    Task { await saveUpdateConfiguration() }
                }
                .adaptiveGlassButtonStyle()
            }

            Divider()

            HStack(spacing: 10) {
                Button {
                    Task { await runUpgradeReadinessCheck() }
                } label: {
                    if readinessBusy {
                        ProgressView().controlSize(.small)
                    } else {
                        Label("运行升级就绪检查", systemImage: "checkmark.shield")
                    }
                }
                .adaptiveGlassButtonStyle()
                .disabled(readinessBusy)
                if let report = readinessReport {
                    Text(report.summary)
                        .font(Theme.appFont(size: 11, weight: .medium))
                        .foregroundStyle(readinessColor(report.status))
                }
                Spacer()
            }

            if let report = readinessReport {
                VStack(alignment: .leading, spacing: 8) {
                    ForEach(report.checks) { check in
                        HStack(alignment: .top, spacing: 8) {
                            Image(systemName: readinessIcon(check.status))
                                .foregroundStyle(readinessColor(check.status))
                                .frame(width: 16)
                            VStack(alignment: .leading, spacing: 2) {
                                Text(check.label)
                                    .font(Theme.appFont(size: 11, weight: .semibold))
                                Text(check.detail)
                                    .font(Theme.appFont(size: 10))
                                    .foregroundStyle(Theme.mutedText)
                            }
                        }
                    }
                }
                .padding(10)
                .background(Theme.soft)
                .clipShape(RoundedRectangle(cornerRadius: 9))
            }

            Text("更新配置、安装入口和升级就绪检查统一保留在此页面。")
                .font(Theme.appFont(size: 10))
                .foregroundStyle(Theme.mutedText)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(18)
        .background(Theme.surface.opacity(0.66))
        .clipShape(RoundedRectangle(cornerRadius: 14, style: .continuous))
    }

    private var releaseNotes: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Text("最近版本更新")
                    .font(Theme.appFont(size: 15, weight: .semibold))
                Spacer()
                Text("v\(marketingVersion)")
                    .font(Theme.appFont(size: 11, weight: .semibold))
                    .foregroundStyle(Theme.accentStrong)
            }
            releaseItem(
                "增量同步更可靠",
                "WebDAV 与本地备份只传输发生变化的内容，并优化大量对话下的数据库读取。"
            )
            releaseItem(
                "设置结构更清晰",
                "同步、数据位置、导入、备份与恢复点已合并到“数据同步与备份”。"
            )
            releaseItem(
                "Agent 覆盖扩展",
                "支持 \(NativeAgentIntegrationStore.catalogCount) 种主流 Agent 与 CLI 检测；已安装项目优先显示，未安装项目保持关闭。"
            )
            releaseItem(
                "原生体验改进",
                "强化单实例窗口、菜单栏、独立设置、状态反馈与本机数据保护。"
            )
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(18)
        .background(Theme.surface.opacity(0.66))
        .clipShape(RoundedRectangle(cornerRadius: 14, style: .continuous))
    }

    private var productDescription: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("关于本软件")
                .font(Theme.appFont(size: 15, weight: .semibold))
            Text(
                """
                AI Memory 是一款本地优先的 AI 工作记忆工具。它将不同 AI Agent 与命令行工具产生的对话历史、记忆规则、检查点和交接信息集中到一个原生工作台，帮助你快速找回上下文、检索过去的工作，并从上次中断的位置继续。

                所有核心数据均由本机管理，兼顾隐私、可靠性与长期可用性。
                """
            )
            .font(Theme.appFont(size: 13))
            .lineSpacing(9)
            .foregroundStyle(Theme.secondaryText)
            .fixedSize(horizontal: false, vertical: true)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(18)
        .background(Theme.surface.opacity(0.66))
        .clipShape(RoundedRectangle(cornerRadius: 14, style: .continuous))
    }

    private var footer: some View {
        VStack(spacing: 5) {
            Text("本地优先 · 独立数据 · 原生 macOS")
                .font(Theme.appFont(size: 11, weight: .medium))
            Text("Copyright © 2026 douxy1994")
                .font(Theme.appFont(size: 11, weight: .medium))
            Text("Licensed under the GNU Affero General Public License v3.0 (AGPL-3.0-only)")
                .font(Theme.appFont(size: 10))
                .foregroundStyle(Theme.mutedText)
            Text("Bundle ID  com.aimemory.app")
                .font(Theme.appFont(size: 10, design: .monospaced))
                .foregroundStyle(Theme.mutedText)
        }
        .foregroundStyle(Theme.secondaryText)
    }

    private func versionPill(_ text: String) -> some View {
        Text(text)
            .font(Theme.appFont(size: 11, weight: .medium))
            .foregroundStyle(Theme.secondaryText)
            .padding(.horizontal, 10)
            .padding(.vertical, 5)
            .background(Theme.soft, in: Capsule())
    }

    private func releaseItem(_ title: String, _ detail: String) -> some View {
        HStack(alignment: .top, spacing: 9) {
            Image(systemName: "checkmark.circle.fill")
                .font(Theme.appFont(size: 12))
                .foregroundStyle(Theme.accent)
                .padding(.top, 1)
            VStack(alignment: .leading, spacing: 2) {
                Text(title)
                    .font(Theme.appFont(size: 12, weight: .semibold))
                Text(detail)
                    .font(Theme.appFont(size: 11))
                    .foregroundStyle(Theme.secondaryText)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
    }

    private var marketingVersion: String {
        Bundle.main.object(
            forInfoDictionaryKey: "CFBundleShortVersionString"
        ) as? String ?? "0.1.2"
    }

    private var buildVersion: String {
        let bundledRevision = Bundle.main.url(
            forResource: "AIMemorySourceRevision",
            withExtension: "txt"
        ).flatMap { try? String(contentsOf: $0, encoding: .utf8) }
        let plistRevision = Bundle.main.object(
            forInfoDictionaryKey: "AIMemorySourceRevision"
        ) as? String
        let revision = (bundledRevision ?? plistRevision)?
            .trimmingCharacters(in: .whitespacesAndNewlines)
        guard let revision,
              !revision.isEmpty,
              revision != "uncommitted"
        else {
            return "未提交构建"
        }
        return revision
    }

    private func loadConfiguration() async {
        do {
            let loadedSettings = try await store.loadSettingsDictionary()
            let feed = (loadedSettings["updateFeedURL"] as? String)
                ?? (loadedSettings["update_feed_url"] as? String)
                ?? ""
            await MainActor.run {
                settings = loadedSettings
                autoCheckUpdates = (loadedSettings["autoCheckUpdates"] as? Bool)
                    ?? (loadedSettings["auto_check_updates"] as? Bool)
                    ?? true
                updateFeedURL = feed.trimmingCharacters(
                    in: .whitespacesAndNewlines
                )
                githubURL = Self.projectURL(from: updateFeedURL)
            }
        } catch {
            await MainActor.run {
                updateStatus = "读取更新配置失败：\(error.localizedDescription)"
            }
        }
    }

    private var autoCheckUpdatesBinding: Binding<Bool> {
        Binding(
            get: { autoCheckUpdates },
            set: { enabled in
                autoCheckUpdates = enabled
                Task { await saveUpdateConfiguration() }
            }
        )
    }

    @MainActor
    private func saveUpdateConfiguration() async {
        var updated = settings
        let feed = updateFeedURL.trimmingCharacters(in: .whitespacesAndNewlines)
        updated["autoCheckUpdates"] = autoCheckUpdates
        updated["updateFeedURL"] = feed
        updated.removeValue(forKey: "auto_check_updates")
        updated.removeValue(forKey: "update_feed_url")
        do {
            settings = try await store.saveSettingsDictionary(updated)
            updateFeedURL = feed
            githubURL = Self.projectURL(from: feed)
            updateStatus = "更新设置已保存。"
        } catch {
            updateStatus = "保存更新设置失败：\(error.localizedDescription)"
        }
    }

    @MainActor
    private func runUpgradeReadinessCheck() async {
        guard !readinessBusy else { return }
        readinessBusy = true
        readinessReport = await store.client.runUpgradeReadinessCheck()
        readinessBusy = false
    }

    private func readinessIcon(_ status: String) -> String {
        switch status {
        case "ok": "checkmark.circle.fill"
        case "error": "xmark.octagon.fill"
        default: "exclamationmark.triangle.fill"
        }
    }

    private func readinessColor(_ status: String) -> Color {
        switch status {
        case "ok": .green
        case "error": .red
        default: .orange
        }
    }

    /// Downloads and installs over the running bundle, then relaunches.
    /// See NativeUpdateInstaller for the atomic swap and rollback behaviour.
    @MainActor
    private func installUpdate(_ release: NativeUpdateRelease) async throws {
        let installer = NativeUpdateInstaller.shared
        let dmgURL = try await installer.download(release) { fraction in
            Task { @MainActor in
                updateStatus =
                    "正在下载 \(release.version)… \(Int(fraction * 100))%"
            }
        }
        updateStatus = "正在校验签名并安装…"
        let outcome = try await Task.detached(priority: .userInitiated) {
            try installer.install(from: dmgURL)
        }.value

        switch outcome {
        case .installed(let appURL, let rollbackURL):
            updateStatus = "已安装 \(release.version)，正在重启…"
            try installer.relaunch(appURL: appURL, rollbackURL: rollbackURL)
        case .openedInstaller(let url):
            updateStatus = """
            当前运行的不是 /Applications/AIMemory.app，无法就地覆盖。\
            已打开 \(url.lastPathComponent)，请手动拖入「应用程序」。
            """
        }
    }

    private func checkForUpdates(automaticInstall: Bool) async {
        guard !updateBusy else { return }
        updateBusy = true
        availableRelease = nil
        defer { updateBusy = false }
        do {
            let result = try await updateService.check(
                feedURL: URL(string: updateFeedURL),
                currentVersion: marketingVersion
            )
            switch result {
            case .current(let release):
                updateStatus =
                    "当前版本已是最新；更新源最新版本为 \(release.version)。"
                githubURL = githubURL ?? Self.projectURL(from: release.pageURL)
                if automaticInstall {
                    showAlert(
                        title: "AI Memory 已是最新版本",
                        message: "当前版本 \(marketingVersion) 已是最新版本。"
                    )
                }
            case .available(let release):
                updateStatus = "发现新版本 \(release.version)：\(release.title)"
                githubURL = githubURL ?? Self.projectURL(from: release.pageURL)
                if automaticInstall {
                    updateStatus = "发现新版本 \(release.version)，正在下载…"
                    try await installUpdate(release)
                } else {
                    availableRelease = release
                }
            }
        } catch {
            updateStatus = "检查更新失败：\(error.localizedDescription)"
            if automaticInstall {
                showAlert(
                    title: "无法检查更新",
                    message: error.localizedDescription
                )
            }
        }
    }

    @MainActor
    private func showAlert(title: String, message: String) {
        let alert = NSAlert()
        alert.alertStyle = .informational
        alert.messageText = title
        alert.informativeText = message
        alert.addButton(withTitle: "好")
        alert.runModal()
    }

    private static func projectURL(from rawValue: String) -> URL? {
        guard let url = URL(string: rawValue) else { return nil }
        return projectURL(from: url)
    }

    private static func projectURL(from url: URL) -> URL? {
        guard let host = url.host?.lowercased(),
              host == "api.github.com" || host == "github.com"
        else { return nil }
        let components = url.pathComponents.filter { $0 != "/" }
        let owner: String
        let repository: String
        if host == "api.github.com" {
            guard components.count >= 3, components[0] == "repos" else {
                return nil
            }
            owner = components[1]
            repository = components[2]
        } else {
            guard components.count >= 2 else { return nil }
            owner = components[0]
            repository = components[1]
        }
        return URL(string: "https://github.com/\(owner)/\(repository)")
    }
}

extension Notification.Name {
    static let requestOpenAboutWindow = Notification.Name(
        "com.aimemory.app.requestOpenAboutWindow"
    )
}
