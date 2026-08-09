// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
import AppKit
import SwiftUI

private enum SettingsCategory: String, CaseIterable, Identifiable {
    case general
    case integrations
    case data
    case advanced

    var id: String { rawValue }

    var title: String {
        switch self {
        case .general: "通用"
        case .integrations: "Agent 集成"
        case .data: "数据同步与备份"
        case .advanced: "高级"
        }
    }

    var subtitle: String {
        switch self {
        case .general: "调整 AI Memory 的语言、字体与日常使用体验。"
        case .integrations: "管理 AI Memory 与本机 AI Agent 的连接。"
        case .data: "连接同步服务、查看数据位置、导入既有数据并管理恢复点。"
        case .advanced: "查看隔离信息并处理需要谨慎操作的功能。"
        }
    }

    var icon: String {
        switch self {
        case .general: "slider.horizontal.3"
        case .integrations: "cpu"
        case .data: "arrow.triangle.2.circlepath"
        case .advanced: "gearshape.2"
        }
    }
}

/// Settings surface. Loads the independent settings.json via the bridge and
/// shows the actual stored values. Write paths (saving) land in Phase C.
struct SettingsView: View {
    @ObservedObject var store: AppStore
    @State private var settings: [String: Any]?
    @State private var appPaths: [String: String] = [:]
    @State private var loadedSettings = false
    @State private var integrations: [AgentIntegrationStatus] = []
    @State private var integrationBusy: String?
    @State private var showUninstallConfirm: String?
    @State private var showUninstallAllConfirm = false
    // Sync form state (initialized from settings on load).
    @State private var webdavScheme = "https"
    @State private var webdavHost = ""
    @State private var webdavPath = ""
    @State private var webdavRemotePath = "chatmem"
    @State private var webdavUser = ""
    @State private var webdavPassword = ""
    @State private var webdavVerifying = false
    @State private var webdavSyncing = false
    @State private var webdavFeedback: WebDAVSyncFeedback?
    @State private var syncFolder = ""
    @State private var launchAtLoginState: LaunchAtLoginState = .disabled
    @State private var launchAtLoginBusy = false
    @State private var selectedCategory: SettingsCategory = .general
    private let launchAtLoginService = NativeLaunchAtLoginService()

    var body: some View {
        HStack(spacing: 0) {
            settingsSidebar

            Divider()

            ScrollView {
                VStack(alignment: .leading, spacing: 28) {
                    VStack(alignment: .leading, spacing: 7) {
                        Text(LocalizedStringKey(selectedCategory.title))
                            .font(Theme.appFont(size: 26, weight: .semibold))
                        Text(LocalizedStringKey(selectedCategory.subtitle))
                            .font(Theme.appFont(size: 13))
                            .foregroundStyle(Theme.secondaryText)
                    }
                    .padding(.bottom, 2)

                if selectedCategory == .data {
                section("数据位置", icon: "internaldrive") {
                    settingsRow("Bundle ID", "com.aimemory.app")
                    settingsRow("数据库", appPaths["db_path"] ?? "（未加载）", mono: true)
                    settingsRow("设置文件", appPaths["settings_path"] ?? "（未加载）", mono: true)
                    settingsRow("存储目录", appPaths["support_dir"] ?? "（未加载）", mono: true)
                    settingsRow("遥测子系统", DataPaths.subsystem)
                    Text("AI Memory 与 ChatMem 完全隔离。两个应用各自拥有独立数据库、设置、Keychain、Application Support 目录，可同时安装运行。")
                        .font(Theme.appFont(size: 11))
                        .foregroundStyle(Theme.mutedText)
                }

                section("从 ChatMem 导入", icon: "arrow.down.doc") {
                    Text("将现有 ChatMem 数据库复制到 AI Memory 的独立数据目录。源文件不会被修改；现有 AI Memory 数据库会先备份。")
                        .font(Theme.appFont(size: 12))
                        .foregroundStyle(Theme.secondaryText)
                    HStack {
                        if let db = DataPaths.chatMemDBURL {
                            Text(db.path)
                                .font(Theme.appFont(size: 10, design: .monospaced))
                                .lineLimit(1).truncationMode(.middle)
                                .foregroundStyle(Theme.mutedText)
                        } else {
                            Text("未检测到 ChatMem 数据库")
                                .font(Theme.appFont(size: 11))
                                .foregroundStyle(Theme.mutedText)
                        }
                        Spacer()
                        Button("导入…") {
                            NotificationCenter.default.post(name: .requestImportFromChatMem, object: nil)
                        }
                        .adaptiveGlassButtonStyle(prominent: true)
                        .disabled(DataPaths.chatMemDBURL == nil)
                    }
                }
                }

                if selectedCategory == .general {
                section("语言与字体", icon: "textformat") {
                    HStack {
                        Text("语言").font(Theme.appFont(size: 13))
                        Spacer()
                        Picker("语言", selection: localeBinding) {
                            Text("简体中文").tag("zh-CN")
                            Text("English").tag("en")
                        }
                        .labelsHidden()
                        .pickerStyle(.menu)
                        .frame(width: 130)
                    }
                    HStack {
                        Text("字体").font(Theme.appFont(size: 13))
                        Spacer()
                        Picker("字体", selection: fontBinding) {
                            ForEach(Theme.FontFamily.allCases) { f in
                                Text(LocalizedStringKey(f.label)).tag(f)
                            }
                        }
                        .labelsHidden()
                        .pickerStyle(.menu)
                        .frame(width: 130)
                    }
                    Text("字体选择会立即应用；如选思源/霞鹜文楷但未安装，会自动回退系统字体。")
                        .font(Theme.appFont(size: 10))
                        .foregroundStyle(Theme.mutedText)
                }

                section("启动与窗口", icon: "power") {
                    HStack(spacing: 14) {
                        VStack(alignment: .leading, spacing: 4) {
                            Text("登录时自动启动")
                                .font(Theme.appFont(size: 13, weight: .medium))
                            Text(launchAtLoginState.detail)
                                .font(Theme.appFont(size: 10))
                                .foregroundStyle(
                                    launchAtLoginState == .requiresApproval
                                        ? Color.orange
                                        : Theme.mutedText
                                )
                        }
                        Spacer()
                        if launchAtLoginBusy {
                            ProgressView().controlSize(.small)
                        }
                        Toggle(
                            "登录时自动启动",
                            isOn: Binding(
                                get: { launchAtLoginState.isEnabled },
                                set: { enabled in
                                    Task { await setLaunchAtLogin(enabled) }
                                }
                            )
                        )
                        .labelsHidden()
                        .toggleStyle(.switch)
                        .disabled(launchAtLoginBusy)
                    }
                    if launchAtLoginState == .requiresApproval
                        || launchAtLoginState == .unavailable {
                        Button("打开登录项系统设置") {
                            launchAtLoginService.openSystemSettings()
                        }
                        .adaptiveGlassButtonStyle()
                        .controlSize(.small)
                    }
                }
                }

                if selectedCategory == .integrations {
                section("Agent 集成", icon: "cpu") {
                    HStack {
                        Text("把 AI Memory 的 MCP 服务和 skill 写入各 agent 配置。安装前会自动备份现有配置。")
                            .font(Theme.appFont(size: 11))
                            .foregroundStyle(Theme.mutedText)
                        Spacer()
                        Button("全部安装") {
                            Task { await installAllIntegrations() }
                        }
                        .adaptiveGlassButtonStyle(prominent: true)
                        .controlSize(.small)
                        .disabled(integrationBusy != nil)
                        Button("全部卸载") {
                            showUninstallAllConfirm = true
                        }
                        .adaptiveGlassButtonStyle()
                        .controlSize(.small)
                        .disabled(integrationBusy != nil)
                        Button(action: {Task { await loadIntegrations() }}) {
                            Label("重新检测", systemImage: "arrow.clockwise")
                        }
                        .adaptiveGlassButtonStyle().controlSize(.small)
                    }
                    if integrations.isEmpty {
                        Text("未检测到 agent 集成状态。点击「重新检测」。")
                            .font(Theme.appFont(size: 12))
                            .foregroundStyle(Theme.mutedText)
                            .padding(.vertical, 4)
                    } else {
                        ForEach(integrations) { integ in
                            VStack(alignment: .leading, spacing: 4) {
                                HStack(spacing: 6) {
                                    Image(systemName: integ.mcpInstalled ? "checkmark.circle.fill" : "circle")
                                        .foregroundStyle(integ.mcpInstalled ? Theme.accent : Theme.mutedText)
                                    Text(integ.label).font(Theme.appFont(size: 13, weight: .medium))
                                    Spacer()
                                    if !integ.isAgentDetected {
                                        Text("本机未安装")
                                            .font(Theme.appFont(size: 10))
                                            .foregroundStyle(Theme.mutedText)
                                    } else if !integ.canInstallIntegration {
                                        Text("已检测")
                                            .font(Theme.appFont(size: 10))
                                            .foregroundStyle(Theme.accentStrong)
                                    } else if integ.mcpInstalled {
                                        Text(integ.statusLabel.isEmpty ? "已安装" : integ.statusLabel)
                                            .font(Theme.appFont(size: 10))
                                            .foregroundStyle(Theme.accentStrong)
                                    } else {
                                        Text("未安装")
                                            .font(Theme.appFont(size: 10))
                                            .foregroundStyle(Theme.mutedText)
                                    }
                                    if integrationBusy == integ.agent {
                                        ProgressView().controlSize(.mini)
                                    } else if integ.mcpInstalled {
                                        Button("卸载") { showUninstallConfirm = integ.agent }
                                            .adaptiveGlassButtonStyle().controlSize(.mini)
                                    } else if integ.isAgentDetected && integ.canInstallIntegration {
                                        Button("安装") {
                                            let a = integ.agent
                                            Task { await runInstall(a) }
                                        }
                                        .adaptiveGlassButtonStyle(prominent: true).controlSize(.mini)
                                    }
                                }
                                if !integ.details.isEmpty {
                                    Text(integ.details.joined(separator: " · "))
                                        .font(Theme.appFont(size: 9))
                                        .foregroundStyle(Theme.mutedText)
                                        .lineLimit(2)
                                }
                                if integ.isAgentDetected && !integ.canInstallIntegration {
                                    Text("暂不支持自动配置")
                                        .font(Theme.appFont(size: 9, weight: .medium))
                                        .foregroundStyle(Color.orange)
                                }
                            }
                            .padding(10)
                            .background(Theme.soft)
                            .clipShape(RoundedRectangle(cornerRadius: 8))
                        }
                    }
                }
                }

                if selectedCategory == .data {
                section("同步", icon: "arrow.triangle.2.circlepath") {
                    // WebDAV form
                    VStack(alignment: .leading, spacing: 14) {
                        Text("WebDAV").font(Theme.appFont(size: 14, weight: .semibold))
                        HStack(alignment: .top, spacing: 14) {
                            VStack(alignment: .leading, spacing: 5) {
                                Text("协议")
                                    .font(Theme.appFont(size: 11))
                                    .foregroundStyle(Theme.mutedText)
                                Picker("协议", selection: $webdavScheme) {
                                    Text("https").tag("https")
                                    Text("http").tag("http")
                                }
                                .labelsHidden()
                                .frame(width: 105, height: 32)
                            }
                            VStack(alignment: .leading, spacing: 5) {
                                Text("服务器")
                                    .font(Theme.appFont(size: 11))
                                    .foregroundStyle(Theme.mutedText)
                                NativeEditableTextField(
                                    text: $webdavHost,
                                    placeholder: "dav.example.com"
                                )
                                .frame(height: 32)
                            }
                        }
                        HStack(alignment: .top, spacing: 14) {
                            webDAVField("服务器路径", text: $webdavPath, placeholder: "例如：webdav")
                            webDAVField("远程目录", text: $webdavRemotePath, placeholder: "chatmem")
                        }
                        HStack(alignment: .top, spacing: 14) {
                            webDAVField("用户名", text: $webdavUser, placeholder: "用户名")
                            VStack(alignment: .leading, spacing: 5) {
                                Text("密码")
                                    .font(Theme.appFont(size: 11))
                                    .foregroundStyle(Theme.mutedText)
                                NativeEditableTextField(
                                    text: $webdavPassword,
                                    placeholder: "密码",
                                    isSecure: true
                                )
                                .frame(height: 32)
                            }
                        }
                        HStack(spacing: 8) {
                            Button {
                                Task { await verifyWebDAVFromSettings() }
                            } label: {
                                HStack(spacing: 5) {
                                    if webdavVerifying {
                                        ProgressView().controlSize(.mini)
                                    }
                                    Text(webdavVerifying ? "正在验证…" : "验证")
                                }
                            }
                            .adaptiveGlassButtonStyle()
                            .controlSize(.small)
                            .disabled(
                                webdavHost.isEmpty || webdavVerifying
                                    || webdavSyncing || store.syncInProgress
                            )

                            Button {
                                Task { await syncWebDAVFromSettings() }
                            } label: {
                                HStack(spacing: 5) {
                                    if webdavSyncing || store.syncInProgress {
                                        ProgressView().controlSize(.mini)
                                    }
                                    Text(
                                        webdavSyncing || store.syncInProgress
                                            ? "正在同步…" : "立即同步"
                                    )
                                }
                            }
                            .adaptiveGlassButtonStyle(prominent: true)
                            .controlSize(.small)
                            .disabled(
                                webdavHost.isEmpty || webdavSyncing
                                    || webdavVerifying || store.syncInProgress
                            )
                            Button("保存") {
                                Task { await persistWebDAVSettings() }
                            }
                            .adaptiveGlassButtonStyle().controlSize(.small)
                            .disabled(
                                webdavVerifying || webdavSyncing || store.syncInProgress
                            )
                            Spacer()
                        }
                        if webdavSyncing || store.syncInProgress {
                            HStack(alignment: .center, spacing: 7) {
                                ProgressView()
                                    .controlSize(.mini)
                                Text(store.syncStatusMessage ?? "WebDAV 正在同步…")
                                    .font(Theme.appFont(size: 11))
                                    .foregroundStyle(Theme.secondaryText)
                                    .fixedSize(horizontal: false, vertical: true)
                                Spacer(minLength: 0)
                            }
                            .padding(.horizontal, 10)
                            .padding(.vertical, 8)
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .background(Theme.accent.opacity(0.09))
                            .clipShape(RoundedRectangle(cornerRadius: 8))
                            .accessibilityIdentifier("webdav-sync-progress")
                        } else if let feedback = webdavFeedback {
                            HStack(alignment: .top, spacing: 7) {
                                Image(systemName: webDAVFeedbackIcon(feedback.kind))
                                    .foregroundStyle(webDAVFeedbackColor(feedback.kind))
                                Text(feedback.message)
                                    .font(Theme.appFont(size: 11))
                                    .foregroundStyle(Theme.secondaryText)
                                    .fixedSize(horizontal: false, vertical: true)
                                Spacer(minLength: 0)
                            }
                            .padding(.horizontal, 10)
                            .padding(.vertical, 8)
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .background(webDAVFeedbackColor(feedback.kind).opacity(0.09))
                            .clipShape(RoundedRectangle(cornerRadius: 8))
                            .accessibilityElement(children: .combine)
                        }
                    }
                    Divider().opacity(0.4)
                    // Local folder sync
                    VStack(alignment: .leading, spacing: 6) {
                        Text("本地文件夹（OneDrive / Google Drive / Dropbox）")
                            .font(Theme.appFont(size: 12, weight: .semibold))
                        HStack(spacing: 8) {
                            TextField("同步文件夹路径", text: $syncFolder, prompt: Text("选择一个共享同步目录"))
                                .textFieldStyle(.roundedBorder)
                            Button(action: {pickSyncFolder()}) {
                                Image(systemName: "folder")
                            }
                            .adaptiveGlassButtonStyle()
                        }
                        HStack(spacing: 8) {
                            Button(action: {Task { await store.syncLocalNow(folder: syncFolder) }}) { Text("立即同步") }
                            .adaptiveGlassButtonStyle(prominent: true).controlSize(.small)
                            .disabled(syncFolder.isEmpty)
                            if !syncFolder.isEmpty {
                                Button(action: {Task { await checkReadiness() }}) { Text("检测云盘状态") }
                                .adaptiveGlassButtonStyle().controlSize(.small)
                            }
                            Spacer()
                        }
                        Text("双向合并：本地独有的对话上传到共享文件夹，远端独有的下载到本地。支持任意同步盘。")
                            .font(Theme.appFont(size: 10))
                            .foregroundStyle(Theme.mutedText)
                    }
                }
                }

                if selectedCategory == .data {
                section("自动备份", icon: "clock.arrow.circlepath") {
                    Toggle(
                        "自动保留记忆恢复点",
                        isOn: boolBinding("autoCaptureMemory", default: true)
                    )
                    Toggle(
                        "定时备份数据库与设置",
                        isOn: boolBinding("autoBackupEnabled", default: false)
                    )
                    Stepper(
                        "备份间隔：\(intSetting("autoBackupIntervalMinutes") ?? 30) 分钟",
                        value: intBinding(
                            "autoBackupIntervalMinutes",
                            default: 30,
                            range: 5...1440
                        ),
                        step: 5
                    )
                    Stepper(
                        "回收站保留：\(intSetting("trashRetentionDays") ?? 14) 天",
                        value: intBinding(
                            "trashRetentionDays",
                            default: 14,
                            range: 1...365
                        )
                    )
                    HStack {
                        Button("立即创建恢复点") {
                            Task { await store.createRecoveryPoint() }
                        }
                        .adaptiveGlassButtonStyle(prominent: true)
                        Button("在 Finder 中显示备份") {
                            let url = DataPaths.supportDir.appendingPathComponent(
                                "backups",
                                isDirectory: true
                            )
                            try? FileManager.default.createDirectory(
                                at: url,
                                withIntermediateDirectories: true
                            )
                            NSWorkspace.shared.activateFileViewerSelecting([url])
                        }
                        .adaptiveGlassButtonStyle()
                    }
                }
                }

                if selectedCategory == .advanced {
                section("危险操作", icon: "exclamationmark.triangle") {
                    Text("删除与卸载操作统一在对应页面执行，并在实际变更前显示确认。")
                        .font(Theme.appFont(size: 12))
                        .foregroundStyle(Theme.secondaryText)
                    Button("打开回收站") {
                        store.openWorkspace(.trash)
                    }
                    .adaptiveGlassButtonStyle()
                }
                }
            }
                .frame(maxWidth: 760, alignment: .leading)
                .padding(.horizontal, 42)
                .padding(.vertical, 34)
                .frame(maxWidth: .infinity, alignment: .center)
            }
        }
        .background(Theme.appBackground)
        .task {
            guard !loadedSettings else { return }
            loadedSettings = true
            await loadSettings()
        }
        .confirmationDialog(
            "卸载该 agent 集成？",
            isPresented: Binding(
                get: { showUninstallConfirm != nil },
                set: { if !$0 { showUninstallConfirm = nil } }
            ),
            titleVisibility: .visible
        ) {
            Button("卸载", role: .destructive) {
                if let a = showUninstallConfirm {
                    Task { await runUninstall(a) }
                    showUninstallConfirm = nil
                }
            }
            Button("取消", role: .cancel) { showUninstallConfirm = nil }
        } message: {
            Text("只移除 AI Memory 写入的 MCP 服务、skill 和引导规则，不影响其他配置。")
        }
        .confirmationDialog(
            "卸载全部 AI Memory Agent 集成？",
            isPresented: $showUninstallAllConfirm,
            titleVisibility: .visible
        ) {
            Button("全部卸载", role: .destructive) {
                Task { await uninstallAllIntegrations() }
            }
            Button("取消", role: .cancel) {}
        } message: {
            Text("将逐个移除 AI Memory 托管的配置块；每个配置的原始备份和其他用户配置均保留。")
        }
    }

    // MARK: - Load

    private func loadSettings() async {
        do {
            let s = try await store.loadSettingsDictionary()
            let paths = [
                "db_path": DataPaths.dbURL.path,
                "settings_path": DataPaths.settingsURL.path,
                "support_dir": DataPaths.supportDir.path,
            ]
            await MainActor.run {
                self.settings = s
                store.setAppSettings(s)
                self.appPaths = paths
                // Populate sync form from stored settings.
                if let sync = s["sync"] as? [String: Any] {
                    self.webdavScheme = (sync["webdavScheme"] as? String)
                        ?? (sync["webdav_scheme"] as? String)
                        ?? "https"
                    self.webdavHost = (sync["webdavHost"] as? String)
                        ?? (sync["webdav_host"] as? String)
                        ?? ""
                    self.webdavPath = (sync["webdavPath"] as? String)
                        ?? (sync["webdav_path"] as? String)
                        ?? ""
                    self.webdavRemotePath = (sync["remotePath"] as? String)
                        ?? (sync["remote_path"] as? String)
                        ?? "chatmem"
                    self.webdavUser = (sync["username"] as? String)
                        ?? (sync["webdav_username"] as? String)
                        ?? ""
                    self.syncFolder = (sync["syncFolder"] as? String)
                        ?? (sync["sync_folder"] as? String)
                        ?? ""
                }
            }
            let storedUsername = await MainActor.run { self.webdavUser }
            if !storedUsername.isEmpty,
               let storedPassword = try await store.client.loadWebDAVPassword(
                   username: storedUsername
               ) {
                await MainActor.run {
                    self.webdavPassword = storedPassword
                }
            }
            await loadIntegrations()
            await MainActor.run {
                self.launchAtLoginState = launchAtLoginService.state
            }
        } catch {
            await MainActor.run {
                self.store.bannerError = "加载设置失败：\(error.localizedDescription)"
            }
        }
    }

    @MainActor
    private func setLaunchAtLogin(_ enabled: Bool) async {
        guard !launchAtLoginBusy else { return }
        launchAtLoginBusy = true
        defer { launchAtLoginBusy = false }
        do {
            launchAtLoginState = try launchAtLoginService.setEnabled(enabled)
            switch launchAtLoginState {
            case .enabled:
                store.flash("已开启登录时自动启动。")
            case .disabled:
                store.flash("已关闭登录时自动启动。")
            case .requiresApproval:
                store.bannerMessage = "请在系统设置中批准 AI Memory 登录项。"
                launchAtLoginService.openSystemSettings()
            case .unavailable:
                store.bannerError = launchAtLoginState.detail
            }
        } catch {
            launchAtLoginState = launchAtLoginService.state
            store.bannerError = "更新登录项失败：\(error.localizedDescription)"
        }
    }

    private func loadIntegrations() async {
        do {
            let list = try await store.client.detectAgentIntegrations()
            await MainActor.run { self.integrations = list }
        } catch {
            await MainActor.run { self.integrations = [] }
        }
    }

    /// Present an NSOpenPanel to pick a sync folder (AppKit interop).
    private func pickSyncFolder() {
        let panel = NSOpenPanel()
        panel.title = "选择同步文件夹"
        panel.message = "OneDrive / Google Drive / Dropbox 或任意共享同步目录。"
        panel.canChooseFiles = false
        panel.canChooseDirectories = true
        panel.allowsMultipleSelection = false
        panel.begin { [self] response in
            if response == .OK, let url = panel.url {
                Task { @MainActor in
                    self.syncFolder = url.path
                    // Persist into settings.json so other surfaces (workbench
                    // "立即同步") can read it.
                    await persistSyncSetting(key: "sync_folder", value: url.path)
                }
            }
        }
    }

    /// Check cloud readiness (lock files, quiet period) for the chosen folder.
    private func checkReadiness() async {
        do {
            let r = try await store.client.checkCloudReadiness(folder: syncFolder)
            let action = r["recommended_action"] as? String ?? "unknown"
            let quiet = r["is_quiet"] as? Bool ?? false
            let locks = r["has_lock_files"] as? Bool ?? false
            let msg: String
            switch action {
            case "safe_to_sync": msg = "云盘空闲，可以安全同步。"
            case "wait": msg = "云盘正在同步（\(locks ? "检测到锁文件" : "未安静")），建议稍后再试。"
            case "folder_missing": msg = "文件夹不存在。"
            default: msg = "状态未知。"
            }
            _ = quiet
            await MainActor.run { store.bannerMessage = msg }
        } catch {
            await MainActor.run { store.bannerError = "检测失败：\(error.localizedDescription)" }
        }
    }

    /// Merge a single sync key into settings.json and persist.
    private func persistSyncSetting(key: String, value: String) async {
        var current = (settings ?? [:])
        var sync = (current["sync"] as? [String: Any]) ?? [:]
        sync[key] = value
        current["sync"] = sync
        do {
            let normalized = try await store.saveSettingsDictionary(current)
            await MainActor.run {
                self.settings = normalized
            }
        } catch {
            await MainActor.run { store.bannerError = "保存设置失败：\(error.localizedDescription)" }
        }
    }

    @discardableResult
    private func persistWebDAVSettings(
        showConfirmation: Bool = true
    ) async -> Bool {
        var current = settings ?? [:]
        var sync = current["sync"] as? [String: Any] ?? [:]
        sync["webdavScheme"] = webdavScheme
        sync["webdavHost"] = webdavHost
        sync["webdavPath"] = webdavPath
        sync["remotePath"] = webdavRemotePath.isEmpty ? "chatmem" : webdavRemotePath
        sync["username"] = webdavUser
        for legacy in [
            "webdav_scheme", "webdav_host", "webdav_path",
            "remote_path", "webdav_username",
        ] {
            sync.removeValue(forKey: legacy)
        }
        current["sync"] = sync
        do {
            if !webdavUser.isEmpty, !webdavPassword.isEmpty {
                try await store.client.saveWebDAVPassword(
                    username: webdavUser,
                    password: webdavPassword
                )
            }
            let normalized = try await store.saveSettingsDictionary(current)
            await MainActor.run {
                settings = normalized
                if showConfirmation {
                    store.bannerMessage = webdavPassword.isEmpty
                        ? "WebDAV 设置已保存；密码未更改。"
                        : "WebDAV 设置和密码已保存。"
                }
            }
            return true
        } catch {
            await MainActor.run {
                store.bannerError = "保存 WebDAV 设置失败：\(error.localizedDescription)"
            }
            return false
        }
    }

    private func verifyWebDAVFromSettings() async {
        await MainActor.run {
            webdavVerifying = true
            webdavFeedback = nil
        }
        let ok = await store.verifyWebDAV(
            scheme: webdavScheme,
            host: webdavHost,
            path: webdavPath,
            remotePath: webdavRemotePath,
            username: webdavUser.isEmpty ? nil : webdavUser,
            password: webdavPassword.isEmpty ? nil : webdavPassword
        )
        if ok {
            let persisted = await persistWebDAVSettings(showConfirmation: false)
            await MainActor.run {
                if persisted {
                    let message = "WebDAV 连接验证成功。"
                    webdavFeedback = WebDAVSyncFeedback(
                        kind: .success,
                        message: message
                    )
                    store.bannerError = nil
                    store.bannerMessage = message
                } else {
                    webdavFeedback = WebDAVSyncFeedback(
                        kind: .failure,
                        message: store.bannerError ?? "WebDAV 配置保存失败。"
                    )
                }
                webdavVerifying = false
            }
        } else {
            await MainActor.run {
                webdavFeedback = WebDAVSyncFeedback(
                    kind: .failure,
                    message: store.bannerError ?? "WebDAV 连接验证失败。"
                )
                webdavVerifying = false
            }
        }
    }

    private func syncWebDAVFromSettings() async {
        await MainActor.run {
            webdavSyncing = true
            webdavFeedback = nil
        }
        let feedback = await store.syncWebDAVNow(
            scheme: webdavScheme,
            host: webdavHost,
            path: webdavPath,
            remotePath: webdavRemotePath,
            username: webdavUser.isEmpty ? nil : webdavUser,
            password: webdavPassword.isEmpty ? nil : webdavPassword
        )
        await MainActor.run {
            webdavFeedback = feedback
            webdavSyncing = false
        }
    }

    private func runInstall(_ agent: String) async {
        await MainActor.run { self.integrationBusy = agent }
        defer { Task { @MainActor in self.integrationBusy = nil } }
        do {
            _ = try await store.client.installAgentIntegration(agent: agent)
            await loadIntegrations()
            await MainActor.run { store.bannerMessage = "已安装 \(agent) 集成。" }
        } catch {
            await MainActor.run { store.bannerError = "安装失败：\(error.localizedDescription)" }
        }
    }

    private func runUninstall(_ agent: String) async {
        await MainActor.run { self.integrationBusy = agent }
        defer { Task { @MainActor in self.integrationBusy = nil } }
        do {
            _ = try await store.client.uninstallAgentIntegration(agent: agent)
            await loadIntegrations()
            await MainActor.run { store.bannerMessage = "已卸载 \(agent) 集成。" }
        } catch {
            await MainActor.run { store.bannerError = "卸载失败：\(error.localizedDescription)" }
        }
    }

    private func installAllIntegrations() async {
        await MainActor.run { integrationBusy = "all" }
        defer { Task { @MainActor in integrationBusy = nil } }
        var failures: [String] = []
        for integration in integrations
        where integration.isAgentDetected
            && integration.canInstallIntegration
            && !integration.mcpInstalled {
            do {
                _ = try await store.client.installAgentIntegration(
                    agent: integration.agent
                )
            } catch {
                failures.append(integration.label)
            }
        }
        await loadIntegrations()
        await MainActor.run {
            if failures.isEmpty {
                store.bannerMessage = "已安装全部可用 Agent 集成。"
            } else {
                store.bannerError = "部分安装失败：\(failures.joined(separator: "、"))"
            }
        }
    }

    private func uninstallAllIntegrations() async {
        await MainActor.run { integrationBusy = "all" }
        defer { Task { @MainActor in integrationBusy = nil } }
        var failures: [String] = []
        for integration in integrations where integration.mcpInstalled {
            do {
                _ = try await store.client.uninstallAgentIntegration(
                    agent: integration.agent
                )
            } catch {
                failures.append(integration.label)
            }
        }
        await loadIntegrations()
        await MainActor.run {
            if failures.isEmpty {
                store.bannerMessage = "已卸载全部 AI Memory Agent 集成。"
            } else {
                store.bannerError = "部分卸载失败：\(failures.joined(separator: "、"))"
            }
        }
    }

    private func webDAVFeedbackIcon(_ kind: WebDAVFeedbackKind) -> String {
        switch kind {
        case .success: "checkmark.circle.fill"
        case .warning: "exclamationmark.triangle.fill"
        case .failure: "xmark.octagon.fill"
        }
    }

    private func webDAVFeedbackColor(_ kind: WebDAVFeedbackKind) -> Color {
        switch kind {
        case .success: .green
        case .warning: .orange
        case .failure: .red
        }
    }

    // MARK: - Accessors

    private func stringSetting(_ key: String) -> String? {
        (settings?[key] as? String)
            ?? canonicalKey(for: key).flatMap { settings?[$0] as? String }
    }

    /// Locale picker binding. Saves on change.
    private var localeBinding: Binding<String> {
        Binding(
            get: { stringSetting("locale") ?? "zh-CN" },
            set: { newValue in
                Task {
                    await store.setLocale(newValue)
                    await loadSettings()
                }
            }
        )
    }

    /// Font family picker binding. Saves + applies on change.
    private var fontBinding: Binding<Theme.FontFamily> {
        Binding(
            get: {
                (stringSetting("fontFamily") ?? stringSetting("font_family"))
                    .flatMap(Theme.FontFamily.init(rawValue:)) ?? .system
            },
            set: { newValue in
                Task {
                    await store.setFontFamily(newValue)
                    await loadSettings()
                }
            }
        )
    }
    private func boolSetting(_ key: String) -> Bool? {
        let value = settings?[key] ?? canonicalKey(for: key).flatMap { settings?[$0] }
        return (value as? Bool) ?? (value as? Int).map { $0 != 0 }
    }
    private func intSetting(_ key: String) -> Int? {
        let value = settings?[key] ?? canonicalKey(for: key).flatMap { settings?[$0] }
        return (value as? Int) ?? (value as? Int64).map(Int.init)
            ?? (value as? Double).map(Int.init)
    }

    private func canonicalKey(for legacyKey: String) -> String? {
        [
            "font_family": "fontFamily",
            "auto_check_updates": "autoCheckUpdates",
            "auto_capture_memory": "autoCaptureMemory",
            "trash_retention_days": "trashRetentionDays",
            "auto_backup_enabled": "autoBackupEnabled",
            "auto_backup_interval_minutes": "autoBackupIntervalMinutes",
            "update_feed_url": "updateFeedURL",
        ][legacyKey]
    }

    private func boolBinding(_ key: String, default defaultValue: Bool) -> Binding<Bool> {
        Binding(
            get: { boolSetting(key) ?? defaultValue },
            set: { value in persistScalarSetting(key: key, value: value) }
        )
    }

    private func intBinding(
        _ key: String,
        default defaultValue: Int,
        range: ClosedRange<Int>
    ) -> Binding<Int> {
        Binding(
            get: { min(range.upperBound, max(range.lowerBound, intSetting(key) ?? defaultValue)) },
            set: { value in
                persistScalarSetting(
                    key: key,
                    value: min(range.upperBound, max(range.lowerBound, value))
                )
            }
        )
    }

    private func persistScalarSetting(key: String, value: Any) {
        var current = settings ?? [:]
        current[key] = value
        let legacy = [
            "autoCheckUpdates": "auto_check_updates",
            "autoCaptureMemory": "auto_capture_memory",
            "trashRetentionDays": "trash_retention_days",
            "autoBackupEnabled": "auto_backup_enabled",
            "autoBackupIntervalMinutes": "auto_backup_interval_minutes",
            "updateFeedURL": "update_feed_url",
        ][key]
        if let legacy { current.removeValue(forKey: legacy) }
        settings = current
        Task {
            do {
                let normalized = try await store.saveSettingsDictionary(current)
                await MainActor.run { self.settings = normalized }
            } catch {
                await MainActor.run {
                    store.bannerError = "保存设置失败：\(error.localizedDescription)"
                }
            }
        }
    }

    private func fontLabel(_ family: String?) -> String {
        switch family ?? "system" {
        case "source-sans": "思源黑体"
        case "source-serif": "思源宋体"
        case "wenkai": "霞鹜文楷"
        default: "系统默认"
        }
    }

    private var syncWebDAVLabel: String {
        guard let sync = settings?["sync"] as? [String: Any] else { return "未配置" }
        let host = (sync["webdavHost"] as? String) ?? (sync["webdav_host"] as? String) ?? ""
        return host.isEmpty ? "未配置" : host
    }
    private var syncLocalLabel: String {
        guard let sync = settings?["sync"] as? [String: Any] else {
            return "未配置"
        }
        let folder = (sync["syncFolder"] as? String) ?? (sync["sync_folder"] as? String) ?? ""
        guard !folder.isEmpty else { return "未配置" }
        return folder
    }
    private var syncOneDriveLabel: String {
        guard let sync = settings?["sync"] as? [String: Any],
              let od = sync["onedrive_folder"] as? String, !od.isEmpty else {
            return "未配置"
        }
        return od
    }

    private var settingsSidebar: some View {
        VStack(alignment: .leading, spacing: 0) {
            Text("设置")
                .font(Theme.appFont(size: 21, weight: .semibold))
                .padding(.horizontal, 20)
                .padding(.top, 28)
                .padding(.bottom, 18)

            VStack(spacing: 4) {
                ForEach(SettingsCategory.allCases) { category in
                    Button {
                        withAnimation(.easeOut(duration: 0.16)) {
                            selectedCategory = category
                        }
                    } label: {
                        HStack(spacing: 11) {
                            Image(systemName: category.icon)
                                .font(Theme.appFont(size: 14, weight: .medium))
                                .foregroundStyle(
                                    selectedCategory == category
                                        ? Theme.accentStrong
                                        : Theme.secondaryText
                                )
                                .frame(width: 22)
                            Text(LocalizedStringKey(category.title))
                                .font(Theme.appFont(size: 13, weight: .medium))
                            Spacer(minLength: 0)
                        }
                        .padding(.horizontal, 12)
                        .frame(height: 40)
                        .contentShape(Rectangle())
                        .background(
                            RoundedRectangle(cornerRadius: 9, style: .continuous)
                                .fill(
                                    selectedCategory == category
                                        ? Theme.selected
                                        : Color.clear
                                )
                        )
                    }
                    .buttonStyle(.plain)
                    .accessibilityIdentifier("settings-category-\(category.rawValue)")
                }
            }
            .padding(.horizontal, 10)

            Spacer()
        }
        .frame(width: 224)
        .background(.ultraThinMaterial)
    }

    // MARK: - Helpers

    private func section<C: View>(_ title: String, icon: String, @ViewBuilder content: () -> C) -> some View {
        VStack(alignment: .leading, spacing: 18) {
            HStack(spacing: 9) {
                Image(systemName: icon)
                    .font(Theme.appFont(size: 14, weight: .semibold))
                    .foregroundStyle(Theme.accent)
                    .frame(width: 20)
                Text(LocalizedStringKey(title))
                    .font(Theme.appFont(size: 16, weight: .semibold))
            }
            content()
        }
        .padding(22)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Theme.surface)
        .overlay(
            RoundedRectangle(cornerRadius: 12, style: .continuous)
                .stroke(Theme.border, lineWidth: 1)
        )
        .clipShape(RoundedRectangle(cornerRadius: 12, style: .continuous))
    }

    private func webDAVField(
        _ label: String,
        text: Binding<String>,
        placeholder: String
    ) -> some View {
        VStack(alignment: .leading, spacing: 5) {
            Text(LocalizedStringKey(label))
                .font(Theme.appFont(size: 11))
                .foregroundStyle(Theme.mutedText)
            NativeEditableTextField(text: text, placeholder: placeholder)
                .frame(height: 36)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    @ViewBuilder
    private func settingsRow(_ label: String, _ value: String, mono: Bool = false) -> some View {
        if mono {
            // Long paths: stack label above the full-width value.
            VStack(alignment: .leading, spacing: 3) {
                Text(LocalizedStringKey(label))
                    .font(Theme.appFont(size: 11))
                    .foregroundStyle(Theme.mutedText)
                Text(value)
                    .font(Theme.appFont(size: 10, design: .monospaced))
                    .foregroundStyle(Theme.secondaryText)
                    .lineLimit(2)
                    .truncationMode(.middle)
                    .fixedSize(horizontal: false, vertical: true)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
            .padding(.vertical, 5)
        } else {
            HStack {
                Text(LocalizedStringKey(label)).font(Theme.appFont(size: 13))
                Spacer()
                Text(value)
                    .font(Theme.appFont(size: 12))
                    .foregroundStyle(Theme.secondaryText)
                    .lineLimit(1)
                    .truncationMode(.tail)
                    .fixedSize(horizontal: false, vertical: true)
            }
            .padding(.vertical, 5)
        }
    }
}

/// Native AppKit text field used for credential and URL settings. It keeps the
/// standard macOS responder chain and supplies the expected edit context menu,
/// so Command-C / Command-V and right-click paste work consistently.
private struct NativeEditableTextField: NSViewRepresentable {
    @Binding var text: String
    let placeholder: String
    var isSecure = false

    func makeCoordinator() -> Coordinator {
        Coordinator(parent: self)
    }

    func makeNSView(context: Context) -> NSTextField {
        let field: NSTextField = isSecure ? NSSecureTextField() : NSTextField()
        field.delegate = context.coordinator
        field.placeholderString = placeholder
        field.isEditable = true
        field.isSelectable = true
        field.isBezeled = true
        field.bezelStyle = .roundedBezel
        field.drawsBackground = true
        field.usesSingleLineMode = true
        field.lineBreakMode = .byTruncatingTail
        field.font = .systemFont(ofSize: 13)
        field.setContentHuggingPriority(.defaultLow, for: .horizontal)
        field.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        field.menu = editMenu()
        return field
    }

    func updateNSView(_ field: NSTextField, context: Context) {
        context.coordinator.parent = self
        if field.stringValue != text {
            field.stringValue = text
        }
        field.placeholderString = placeholder
    }

    private func editMenu() -> NSMenu {
        let menu = NSMenu()
        [
            ("剪切", #selector(NSText.cut(_:))),
            ("复制", #selector(NSText.copy(_:))),
            ("粘贴", #selector(NSText.paste(_:))),
        ].forEach { title, action in
            let item = NSMenuItem(title: title, action: action, keyEquivalent: "")
            item.target = nil
            menu.addItem(item)
        }
        menu.addItem(.separator())
        let selectAll = NSMenuItem(
            title: "全选",
            action: #selector(NSText.selectAll(_:)),
            keyEquivalent: ""
        )
        selectAll.target = nil
        menu.addItem(selectAll)
        return menu
    }

    final class Coordinator: NSObject, NSTextFieldDelegate {
        var parent: NativeEditableTextField

        init(parent: NativeEditableTextField) {
            self.parent = parent
        }

        func controlTextDidChange(_ notification: Notification) {
            guard let field = notification.object as? NSTextField else { return }
            parent.text = field.stringValue
        }
    }
}

// MARK: - Import-from-ChatMem notification (bridges SwiftUI → AppKit AppDelegate)

extension Notification.Name {
    static let requestImportFromChatMem = Notification.Name("com.aimemory.app.requestImportFromChatMem")
    static let interfaceLocaleDidChange = Notification.Name(
        "com.aimemory.app.interfaceLocaleDidChange"
    )
}
