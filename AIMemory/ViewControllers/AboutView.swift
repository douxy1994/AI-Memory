import AppKit
import SwiftUI

struct AboutView: View {
    @ObservedObject var store: AppStore
    @State private var updateFeedURL = ""
    @State private var githubURL: URL?
    @State private var updateStatus = "尚未检查更新"
    @State private var updateBusy = false
    private let updateService = NativeUpdateService()

    var body: some View {
        ScrollView {
            VStack(spacing: 22) {
                hero
                actionBar
                updateStatusLine
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
            .buttonStyle(.bordered)
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
            .buttonStyle(.borderedProminent)
            .disabled(updateBusy)
        }
    }

    private var updateStatusLine: some View {
        VStack(spacing: 4) {
            Text(updateStatus)
                .font(Theme.appFont(size: 11, weight: .medium))
                .foregroundStyle(Theme.secondaryText)
                .multilineTextAlignment(.center)
            if updateFeedURL.isEmpty {
                Text("可在“设置 → 更新与诊断”中配置项目 GitHub Releases。")
                    .font(Theme.appFont(size: 10))
                    .foregroundStyle(Theme.mutedText)
            }
        }
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
                "支持 41 种主流 Agent 与 CLI 检测；已安装项目优先显示，未安装项目保持关闭。"
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
        ) as? String ?? "0.1.0"
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
            let settings = try await store.loadSettingsDictionary()
            let feed = (settings["updateFeedURL"] as? String)
                ?? (settings["update_feed_url"] as? String)
                ?? ""
            await MainActor.run {
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

    private func checkForUpdates(automaticInstall: Bool) async {
        guard !updateBusy else { return }
        updateBusy = true
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
                    updateStatus = "发现新版本 \(release.version)，正在下载安装包…"
                    let installerURL = try await updateService.downloadAndOpen(
                        release
                    )
                    updateStatus =
                        "版本 \(release.version) 已下载，并已打开 \(installerURL.lastPathComponent)。"
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
