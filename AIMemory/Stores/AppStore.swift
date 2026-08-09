// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
import Foundation
import Combine

/// Central app state. Bridges between the SwiftUI views and the
/// `BridgeClient`. Loads conversation lists lazily per-agent and caches
/// conversation details on demand.
@MainActor
final class AppStore: ObservableObject {
    let client: BridgeClient
    let settingsStore: NativeSettingsStore
    let telemetry = Telemetry()
    private let backupService = NativeBackupService()
    private let updateService = NativeUpdateService()

    // MARK: - Published state

    @Published private(set) var sources: [ConversationSourceStatus] = []
    @Published private(set) var selectedAgent: AgentKind = .codex
    @Published private(set) var conversations: [AgentKind: [ConversationSummary]] = [:]
    @Published private(set) var detailCache: [String: ConversationDetail] = [:]

    @Published var selectedConversationID: String?
    @Published var searchQuery = "" {
        didSet { scheduleSearch() }
    }
    @Published private(set) var searchResults: [ConversationSummary]?
    @Published var workspace: WorkspaceDestination = .workbench
    @Published var memoryDrawerOpen = false
    @Published var memoryDrawerTab: MemoryDrawerTab = .review

    @Published private(set) var loading: LoadState = .idle
    @Published private(set) var repoHealth: RepoHealth?
    @Published var bannerMessage: String?
    @Published var bannerError: String?
    @Published private(set) var syncInProgress = false
    @Published private(set) var syncStatusMessage: String?
    @Published private(set) var syncStatusKind: WebDAVFeedbackKind?
    /// Incremented by the application-menu "检查更新…" command. The About
    /// window observes this value so the command still works when that
    /// window is being created for the first time.
    @Published private(set) var aboutUpdateCheckRequest = 0

    /// Set once the launch check finds a newer release. Drives the download
    /// button next to the brand title, which stays visible until the update is
    /// installed — unlike a banner, which the user can easily miss.
    @Published private(set) var availableUpdate: NativeUpdateRelease?
    @Published private(set) var updateInstalling = false
    @Published private(set) var updateProgress: Double = 0
    @Published private(set) var updateStage: String?

    /// Cached app settings (locale, font, sync config, etc.). Loaded on
    /// bootstrap and after save. Used by the workbench "立即同步" entry to
    /// decide which sync to run.
    @Published private(set) var appSettings: [String: Any]?

    var interfaceLocale: Locale {
        let value = (appSettings?["locale"] as? String) ?? "zh-CN"
        return Locale(identifier: value == "en" ? "en" : "zh-Hans")
    }

    /// The repo whose memory/checkpoints/handoffs are shown in Review /
    /// MemoryDrawer / LocalHistory. Defaults to the home directory's repo
    /// (a common catch-all for un-project-scoped conversations).
    @Published var activeRepoRoot: String = FileManager.default.homeDirectoryForCurrentUser.path

    // Repo-scoped memory data, loaded on demand.
    @Published private(set) var candidates: [MemoryCandidate] = []
    @Published private(set) var approvedMemories: [ApprovedMemory] = []
    @Published private(set) var wikiPages: [WikiPage] = []
    @Published private(set) var checkpoints: [Checkpoint] = []
    @Published private(set) var handoffs: [HandoffPacket] = []
    @Published private(set) var activeRuns: [AgentRunRecord] = []
    @Published private(set) var runArtifacts: [RunArtifactRecord] = []
    @Published private(set) var episodes: [EpisodeRecord] = []
    @Published private(set) var memoryConflicts: [MemoryConflictRecord] = []
    @Published private(set) var entityGraph = MemoryEntityGraph(
        entities: [],
        links: []
    )
    @Published private(set) var trashed: [TrashRecord] = []

    /// Repos that currently have pending-review candidates (for the Review
    /// page's repo picker + auto-default). Loaded on bootstrap.
    @Published private(set) var reposWithCandidates: [RepoCandidateCount] = []

    @Published private(set) var memoryLoading: Bool = false

    private var listRequestIDs: [AgentKind: Int] = [:]
    private var detailRequestID = 0
    private var memoryRequestID = 0
    private var searchTask: Task<Void, Never>?
    private var automaticBackupTask: Task<Void, Never>?
    private var automaticCaptureTask: Task<Void, Never>?
    private var didRunAutomaticUpdateCheck = false

    // Sidebar arrangement + sort state (mirrors ChatMem's organize menu).
    @Published var arrangeMode: ArrangeMode = .byProject
    @Published var sortMode: SortMode = .updatedDesc
    @Published var projectFilters: Set<String> = []   // projectPathKey set; empty = all
    @Published var expandedProjects: [String: Bool] = [:]  // groupKey → expanded
    @Published var allProjectsCollapsed = false
    @Published var bulkSelectionMode = false
    @Published var selectedConversationKeys: Set<String> = []

    /// Candidates pending review only (the actionable subset).
    var pendingCandidates: [MemoryCandidate] {
        candidates.filter { $0.isActionable }
    }

    enum LoadState: Equatable {
        case idle
        case loading(String?)
        case ready
        case failed(String)
    }

    // MARK: - Init

    init(
        client: BridgeClient,
        settingsStore: NativeSettingsStore = NativeSettingsStore()
    ) {
        self.client = client
        self.settingsStore = settingsStore
        // Open once to apply idempotent schema migrations, then close. Keeping
        // a long-lived connection would prevent atomic database replacement
        // during a ChatMem import.
        _ = try? NativeDatabase()
    }

    // MARK: - Bootstrap

    /// App-launch bootstrap: show the existing local index immediately, then
    /// import every supported installed source and refresh all conversations.
    func bootstrap() async {
        loading = .loading("正在读取本地索引…")
        do {
            let detected = try await client.detectSources()
            let cachedAgents = detected.compactMap { status in
                status.available ? status.agentKind : nil
            }
            sources = detected.filter(\.available)
            let preferredAgent = cachedAgents.contains(selectedAgent)
                ? selectedAgent
                : (cachedAgents.first ?? AgentKind.codex)
            selectedAgent = preferredAgent
            await loadConversations(for: preferredAgent)
            loading = .ready
            // Network-only and independent of local history. Starting now keeps
            // the title update affordance from waiting for the full disk scan.
            Task { await checkForUpdatesAtLaunchIfNeeded() }

            // The interface is now usable from the independent local index.
            // Refresh source histories automatically without requiring either
            // workbench refresh button.
            syncInProgress = true
            syncStatusKind = nil
            syncStatusMessage = "正在自动同步本机 agent 记录…"
            let syncReport = try await client.synchronizeInstalledAgentHistory()
            let installedAgents = applyInstalledHistorySync(syncReport)
            if !installedAgents.contains(selectedAgent) {
                selectedAgent = installedAgents.first ?? AgentKind.codex
            }
            for agent in installedAgents {
                conversations[agent] = nil
                await loadConversations(for: agent)
            }
            syncInProgress = false
            loading = .ready
            // Background loads (serial to avoid lock contention).
            await autoSelectActiveRepo()
            await loadRepoMemory(repoRoot: activeRepoRoot)
            await loadTrashed()
            await loadAppSettings()
            if await importChatMemWebDAVIfNeeded() {
                await loadAppSettings()
            }
            telemetry.lifecycle(
                "bootstrap done: \(installedAgents.count) installed sources, "
                    + "\(syncReport.total) conversations indexed, "
                    + "\(selectedAgent.label) selected, repo=\(activeRepoRoot)"
            )
        } catch {
            syncInProgress = false
            loading = .failed(error.localizedDescription)
            bannerError = error.localizedDescription
            telemetry.bridgeError("bootstrap failed: \(error)")
        }
    }

    // MARK: - Source selection

    func requestAboutUpdateCheck() {
        aboutUpdateCheckRequest &+= 1
    }

    /// Select an agent and start loading it if needed.
    func selectAgent(_ agent: AgentKind) {
        automaticCaptureTask?.cancel()
        automaticCaptureTask = nil
        selectedAgent = agent
        selectedConversationID = nil
        bulkSelectionMode = false
        selectedConversationKeys.removeAll()
        searchResults = nil
        scheduleSearch()
        if workspace == .conversation { workspace = .workbench }
        if conversations[agent] == nil {
            Task { await loadConversations(for: agent) }
        }
    }

    func loadConversations(for agent: AgentKind) async {
        let requestID = (listRequestIDs[agent] ?? 0) + 1
        listRequestIDs[agent] = requestID
        if selectedAgent == agent {
            loading = .loading("加载 \(agent.label) 对话…")
        }
        telemetry.bridge("loadConversations START for \(agent.rawValue)")
        do {
            let list = try await client.listConversations(agent: agent.rawValue)
            guard listRequestIDs[agent] == requestID else { return }
            telemetry.bridge("loadConversations GOT \(list.count) for \(agent.rawValue)")
            conversations[agent] = list
            if selectedAgent == agent {
                if selectedConversationID == nil {
                    selectedConversationID = list.first?.id
                }
                loading = .ready
                if !searchQuery.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                    scheduleSearch()
                }
            }
        } catch {
            guard listRequestIDs[agent] == requestID else { return }
            telemetry.bridgeError("loadConversations FAILED: \(error)")
            if selectedAgent == agent {
                self.loading = .failed(error.localizedDescription)
                bannerError = "加载 \(agent.label) 对话失败：\(error.localizedDescription)"
            }
        }
    }

    func reloadCurrentAgent() async {
        guard !syncInProgress else { return }
        syncInProgress = true
        defer { syncInProgress = false }
        syncStatusKind = nil
        syncStatusMessage = "正在刷新 \(selectedAgent.label) 记录…"
        let report = await client.refreshLocalHistory(agent: selectedAgent)
        conversations[selectedAgent] = nil
        await loadConversations(for: selectedAgent)
        let count = report.imported[selectedAgent.rawValue] ?? 0
        if report.warnings.isEmpty {
            let message = "已刷新 \(selectedAgent.label)：本次扫描 \(count) 条对话。"
            syncStatusKind = .success
            syncStatusMessage = message
            flash(message)
        } else {
            let message = "\(selectedAgent.label) 刷新完成，\(report.warnings.count) 项需检查。"
            syncStatusKind = .warning
            syncStatusMessage = message
            bannerError = message
        }
    }

    // MARK: - Derived views

    var currentConversations: [ConversationSummary] {
        conversations[selectedAgent] ?? []
    }

    var filteredConversations: [ConversationSummary] {
        let base = sort(searchResults ?? currentConversations)
        // Apply project filter (from organize menu).
        if projectFilters.isEmpty { return base }
        return base.filter { projectFilters.contains(Self.projectPathKey($0.projectDir)) }
    }

    /// Conversations sorted by the current sort mode.
    var sortedCurrentConversations: [ConversationSummary] {
        sort(currentConversations)
    }

    private func sort(_ base: [ConversationSummary]) -> [ConversationSummary] {
        switch sortMode {
        case .updatedDesc: return base.sorted { $0.updatedAt > $1.updatedAt }
        case .createdDesc: return base.sorted { $0.createdAt > $1.createdAt }
        case .titleAsc:    return base.sorted { $0.displayTitle < $1.displayTitle }
        }
    }

    private func scheduleSearch() {
        searchTask?.cancel()
        let query = searchQuery.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !query.isEmpty else {
            searchResults = nil
            return
        }

        let agent = selectedAgent
        searchTask = Task { [weak self] in
            do {
                try await Task.sleep(for: .milliseconds(180))
                guard !Task.isCancelled, let self else { return }
                let results = try await self.client.searchConversations(
                    agent: agent.rawValue,
                    query: query
                )
                guard !Task.isCancelled,
                      self.selectedAgent == agent,
                      self.searchQuery.trimmingCharacters(in: .whitespacesAndNewlines) == query
                else { return }
                self.searchResults = results
            } catch is CancellationError {
                return
            } catch {
                guard let self, self.selectedAgent == agent else { return }
                self.bannerError = "搜索失败：\(error.localizedDescription)"
            }
        }
    }

    // MARK: - Project grouping (mirrors ChatMem projectGroups memo, App.tsx ~3233)

    /// All available project directories from the current agent's conversations.
    var availableProjects: [String] {
        var seen = Set<String>()
        var result: [String] = []
        for conv in sortedCurrentConversations {
            let dir = conv.projectDir
            let key = Self.projectPathKey(dir)
            if !seen.contains(key) {
                seen.insert(key)
                result.append(dir)
            }
        }
        return result.sorted()
    }

    /// Group filtered conversations by project directory.
    /// Mirrors ChatMem's `projectGroups` useMemo (App.tsx ~3233).
    var projectGroups: [ProjectGroup] {
        var groups: [String: ProjectGroup] = [:]
        for conv in filteredConversations {
            let dir = conv.projectDir
            guard !dir.isEmpty else { continue }
            let key = Self.projectPathKey(dir)
            if var existing = groups[key] {
                existing.conversations.append(conv)
                if conv.updatedAt > existing.latestAt { existing.latestAt = conv.updatedAt }
                groups[key] = existing
            } else {
                groups[key] = ProjectGroup(
                    id: key,
                    label: Self.projectLabel(dir),
                    fullPath: dir,
                    latestAt: conv.updatedAt,
                    conversations: [conv]
                )
            }
        }
        return groups.values.sorted { $0.latestAt > $1.latestAt }
    }

    /// Group project groups by machine/platform.
    /// Mirrors ChatMem's `machineGroups` useMemo (App.tsx ~3310).
    var machineGroups: [MachineGroup] {
        var groups: [String: MachineGroup] = [:]
        for pg in projectGroups {
            let mid = machineGroupOverrides[pg.fullPath]
                ?? Self.detectMachineId(pg.fullPath)
            if var existing = groups[mid] {
                existing.projects.append(pg)
                if pg.latestAt > existing.latestAt { existing.latestAt = pg.latestAt }
                groups[mid] = existing
            } else {
                groups[mid] = MachineGroup(id: mid, label: "", latestAt: pg.latestAt, projects: [pg])
            }
        }
        var result = Array(groups.values)

        // Apply user names before generating default labels.
        for index in result.indices {
            if let custom = machineGroupNames[result[index].id],
               !custom.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                result[index].label = custom
            }
        }

        // Auto-generate labels.
        let platformLabels: [String: String] = [
            "windows": "Windows", "macos": "Mac", "linux": "Linux",
            "internal": "Internal", "other": "Other",
        ]
        var counts: [String: Int] = [:]
        for g in result { counts[g.id, default: 0] += 1 }
        var seen: [String: Int] = [:]
        for i in result.indices {
            if result[i].label.isEmpty {
                let total = counts[result[i].id] ?? 1
                let idx = seen[result[i].id, default: 0]
                seen[result[i].id] = idx + 1
                let base = platformLabels[result[i].id] ?? result[i].id
                result[i].label = total > 1 ? "\(base)-\(idx + 1)" : base
            }
        }
        result.sort { $0.latestAt > $1.latestAt }
        return result
    }

    /// True when there are conversations from more than one machine/platform.
    var hasMultipleMachines: Bool { machineGroups.count > 1 }

    var machineGroupNames: [String: String] {
        (appSettings?["machineGroupNames"] as? [String: String])
            ?? (appSettings?["machine_group_names"] as? [String: String])
            ?? [:]
    }

    var machineGroupOverrides: [String: String] {
        (appSettings?["machineGroupOverrides"] as? [String: String])
            ?? (appSettings?["machine_group_overrides"] as? [String: String])
            ?? [:]
    }

    func renameMachineGroup(id: String, label: String) async {
        let trimmed = label.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }
        var names = machineGroupNames
        names[id] = trimmed
        await saveMachineGrouping(names: names, overrides: machineGroupOverrides)
    }

    func mergeMachineGroup(sourceID: String, into targetID: String) async {
        guard sourceID != targetID,
              let source = machineGroups.first(where: { $0.id == sourceID }) else { return }
        var overrides = machineGroupOverrides
        for project in source.projects {
            overrides[project.fullPath] = targetID
        }
        await saveMachineGrouping(names: machineGroupNames, overrides: overrides)
    }

    func moveProjectToMachine(projectPath: String, targetID: String) async {
        var overrides = machineGroupOverrides
        overrides[projectPath] = targetID
        await saveMachineGrouping(names: machineGroupNames, overrides: overrides)
    }

    func resetMachineGrouping() async {
        await saveMachineGrouping(names: machineGroupNames, overrides: [:])
    }

    private func saveMachineGrouping(
        names: [String: String],
        overrides: [String: String]
    ) async {
        var current = appSettings ?? [:]
        current["machineGroupNames"] = names
        current["machineGroupOverrides"] = overrides
        current.removeValue(forKey: "machine_group_names")
        current.removeValue(forKey: "machine_group_overrides")
        do {
            _ = try await saveSettingsDictionary(current)
            flash("电脑分组已保存。")
        } catch {
            bannerError = "保存电脑分组失败：\(error.localizedDescription)"
        }
    }

    // MARK: - Project expand/collapse (mirrors ChatMem expandedProjects state)

    func isProjectExpanded(_ groupKey: String) -> Bool {
        if let v = expandedProjects[groupKey] { return v }
        return !allProjectsCollapsed   // default: expanded unless all collapsed
    }

    func toggleProjectExpanded(_ groupKey: String) {
        expandedProjects[groupKey] = !isProjectExpanded(groupKey)
    }

    func collapseAllProjects() {
        allProjectsCollapsed = true
        expandedProjects = [:]
    }

    func restoreAllProjects() {
        allProjectsCollapsed = false
        expandedProjects = [:]
    }

    // MARK: - Project path utilities (mirrors ChatMem helper functions)

    /// Normalize a project path: trim trailing slashes, replace backslashes.
    static func normalizeProjectPath(_ path: String) -> String {
        var p = path.replacingOccurrences(of: "\\", with: "/")
        while p.hasSuffix("/") && p.count > 1 { p.removeLast() }
        return p
    }

    /// Get the leaf name of a project path (getProjectLabel, App.tsx ~584).
    static func projectLabel(_ path: String) -> String {
        let trimmed = normalizeProjectPath(path)
        let segments = trimmed.split(separator: "/").map(String.init)
        return segments.last ?? path
    }

    /// A stable key for a project path (for grouping + dedup).
    static func projectPathKey(_ path: String) -> String {
        normalizeProjectPath(path).lowercased()
    }

    /// Detect the machine/platform from a project path (App.tsx ~621).
    static func detectMachineId(_ projectDir: String) -> String {
        let normalized = projectDir.replacingOccurrences(of: "\\", with: "/")
        // Windows: C:/Users/xxx
        if normalized.range(of: #"^[a-zA-Z]:/"#, options: .regularExpression) != nil { return "windows" }
        // macOS: /Users/xxx, /Volumes/xxx, /Applications
        if normalized.range(of: #"^/(Users|Volumes|Applications)/"#, options: .regularExpression) != nil
            || normalized == "/Applications" { return "macos" }
        // Linux
        if normalized.range(of: #"^/(home|root|usr|opt|tmp)/"#, options: .regularExpression) != nil { return "linux" }
        // ChatMem internal
        if normalized.hasPrefix("chatmem://") { return "internal" }
        return "other"
    }

    var selectedConversation: ConversationDetail? {
        guard let id = selectedConversationID else { return nil }
        return detailCache[Self.detailKey(agent: selectedAgent.rawValue, id: id)]
    }

    var selectedSummary: ConversationSummary? {
        guard let id = selectedConversationID else { return nil }
        return currentConversations.first { $0.id == id }
    }

    // MARK: - Detail loading

    /// Select a conversation. Synchronous (safe from button actions); spawns
    /// a detached task for the detail fetch.
    func selectConversation(_ id: String) {
        automaticCaptureTask?.cancel()
        automaticCaptureTask = nil
        selectedConversationID = id
        workspace = .conversation
        let agent = selectedAgent.rawValue
        let cacheKey = Self.detailKey(agent: agent, id: id)
        if let detail = detailCache[cacheKey] {
            Task {
                await activateConversationContext(
                    detail,
                    agent: selectedAgent,
                    id: id
                )
            }
        } else {
            detailRequestID &+= 1
            let requestID = detailRequestID
            loading = .loading("读取对话详情…")
            Task {
                do {
                    let detail = try await client.readConversation(agent: agent, id: id)
                    self.detailCache[cacheKey] = detail
                    if self.detailRequestID == requestID,
                       self.selectedAgent.rawValue == agent,
                       self.selectedConversationID == id {
                        self.loading = .ready
                        await self.activateConversationContext(
                            detail,
                            agent: AgentKind(rawValue: agent) ?? self.selectedAgent,
                            id: id
                        )
                    }
                } catch {
                    guard self.detailRequestID == requestID,
                          self.selectedAgent.rawValue == agent,
                          self.selectedConversationID == id else { return }
                    self.bannerError = "读取对话失败：\(error.localizedDescription)"
                    self.loading = .failed(error.localizedDescription)
                }
            }
        }
    }

    private func activateConversationContext(
        _ detail: ConversationDetail,
        agent: AgentKind,
        id: String
    ) async {
        guard selectedAgent == agent, selectedConversationID == id else { return }
        telemetry.memory("automatic capture context activated for \(agent.rawValue)")
        let project = detail.projectDir.trimmingCharacters(
            in: .whitespacesAndNewlines
        )
        if !project.isEmpty, activeRepoRoot != project {
            activeRepoRoot = project
            await loadRepoMemory(repoRoot: project)
            await refreshRepoHealth(repoRoot: project)
        }
        scheduleAutomaticCapture(agent: agent, id: id)
    }

    private var automaticCaptureEnabled: Bool {
        (appSettings?["autoCaptureMemory"] as? Bool)
            ?? (appSettings?["auto_capture_memory"] as? Bool)
            ?? true
    }

    private func scheduleAutomaticCapture(agent: AgentKind, id: String) {
        automaticCaptureTask?.cancel()
        guard automaticCaptureEnabled else {
            telemetry.memory("automatic capture disabled")
            automaticCaptureTask = nil
            return
        }
        telemetry.memory("automatic capture scheduled for \(agent.rawValue)")
        automaticCaptureTask = Task { [weak self] in
            do {
                try await Task.sleep(for: .milliseconds(350))
                while !Task.isCancelled {
                    guard let self,
                          self.automaticCaptureEnabled,
                          self.selectedAgent == agent,
                          self.selectedConversationID == id else {
                        return
                    }
                    do {
                        let result = try await self.client.autoCaptureConversation(
                            agent: agent,
                            id: id,
                            repoRoot: self.activeRepoRoot
                        )
                        let cacheKey = Self.detailKey(agent: agent.rawValue, id: id)
                        self.detailCache[cacheKey] = result.detail
                        self.checkpoints = [
                            result.checkpoint,
                        ] + self.checkpoints.filter {
                            $0.checkpointID != result.checkpoint.checkpointID
                        }
                        self.telemetry.memory(
                            "automatic capture completed for \(agent.rawValue)"
                        )
                    } catch {
                        self.telemetry.memory(
                            "automatic capture skipped for \(agent.rawValue): \(error)"
                        )
                    }
                    try await Task.sleep(for: .seconds(120))
                }
            } catch is CancellationError {
                return
            } catch {
                self?.telemetry.memory("automatic capture stopped: \(error)")
            }
        }
    }

    private static func detailKey(agent: String, id: String) -> String {
        "\(agent):\(id)"
    }

    // MARK: - Project memory / repo health

    func refreshRepoHealth(repoRoot: String) async {
        do {
            let health = try await client.getRepoMemoryHealth(repoRoot: repoRoot)
            await MainActor.run { self.repoHealth = health }
        } catch {
            await MainActor.run { self.repoHealth = nil }
        }
    }

    // MARK: - Repo-scoped memory data

    /// Load all memory surfaces for the given repo. Safe to call repeatedly.
    func loadRepoMemory(repoRoot: String) async {
        memoryRequestID &+= 1
        let requestID = memoryRequestID
        memoryLoading = true

        async let cands = capture { try await self.client.listMemoryCandidates(repoRoot: repoRoot) }
        async let approved = capture { try await self.client.listRepoMemories(repoRoot: repoRoot) }
        async let wiki = capture { try await self.client.listWikiPages(repoRoot: repoRoot) }
        async let cps = capture { try await self.client.listCheckpoints(repoRoot: repoRoot) }
        async let hds = capture { try await self.client.listHandoffs(repoRoot: repoRoot) }
        async let runs = capture { try await self.client.listActiveRuns(repoRoot: repoRoot) }
        async let artifacts = capture { try await self.client.listRunArtifacts(repoRoot: repoRoot) }
        async let eps = capture { try await self.client.listEpisodes(repoRoot: repoRoot) }
        async let conflicts = capture { try await self.client.listMemoryConflicts(repoRoot: repoRoot) }
        async let graph = capture { try await self.client.listEntityGraph(repoRoot: repoRoot) }
        let results = await (cands, approved, wiki, cps, hds)
        let timeline = await (runs, artifacts, eps, conflicts, graph)

        guard memoryRequestID == requestID, activeRepoRoot == repoRoot else { return }
        var failures: [String] = []
        switch results.0 {
        case .success(let value): candidates = value
        case .failure(let error): failures.append("候选规则：\(error.localizedDescription)")
        }
        switch results.1 {
        case .success(let value): approvedMemories = value
        case .failure(let error): failures.append("启动规则：\(error.localizedDescription)")
        }
        switch results.2 {
        case .success(let value): wikiPages = value
        case .failure(let error): failures.append("Wiki：\(error.localizedDescription)")
        }
        switch results.3 {
        case .success(let value): checkpoints = value
        case .failure(let error): failures.append("检查点：\(error.localizedDescription)")
        }
        switch results.4 {
        case .success(let value): handoffs = value
        case .failure(let error): failures.append("交接包：\(error.localizedDescription)")
        }
        switch timeline.0 {
        case .success(let value): activeRuns = value
        case .failure(let error): failures.append("运行：\(error.localizedDescription)")
        }
        switch timeline.1 {
        case .success(let value): runArtifacts = value
        case .failure(let error): failures.append("产物：\(error.localizedDescription)")
        }
        switch timeline.2 {
        case .success(let value): episodes = value
        case .failure(let error): failures.append("经历：\(error.localizedDescription)")
        }
        switch timeline.3 {
        case .success(let value): memoryConflicts = value
        case .failure(let error): failures.append("冲突：\(error.localizedDescription)")
        }
        switch timeline.4 {
        case .success(let value): entityGraph = value
        case .failure(let error): failures.append("实体图：\(error.localizedDescription)")
        }
        memoryLoading = false
        if failures.count == 10 {
            bannerError = "项目记忆加载失败：\(failures.joined(separator: "；"))"
        }
    }

    func promoteCheckpoint(
        _ checkpoint: Checkpoint,
        toAgent: AgentKind,
        targetProfile: String? = nil
    ) async {
        do {
            _ = try await client.resumeFromCheckpoint(
                checkpointID: checkpoint.checkpointID,
                toAgent: toAgent.rawValue,
                targetProfile: targetProfile
            )
            await loadRepoMemory(repoRoot: activeRepoRoot)
            flash("检查点已提升为 \(toAgent.label) 交接包。")
        } catch {
            bannerError = "恢复检查点失败：\(error.localizedDescription)"
        }
    }

    func createCheckpointForSelectedConversation() async {
        guard let detail = selectedConversation else {
            bannerError = "请先选择一个对话。"
            return
        }
        do {
            let summary = detail.summary?.trimmingCharacters(
                in: .whitespacesAndNewlines
            )
            _ = try await client.createCheckpoint(
                repoRoot: detail.projectDir.isEmpty ? activeRepoRoot : detail.projectDir,
                conversationID: detail.id,
                sourceAgent: detail.sourceAgent,
                summary: summary?.isEmpty == false ? summary! : detail.id,
                resumeCommand: detail.resumeCommand,
                metadataJSON: #"{"message_count":\#(detail.messages.count)}"#
            )
            activeRepoRoot = detail.projectDir.isEmpty ? activeRepoRoot : detail.projectDir
            await loadRepoMemory(repoRoot: activeRepoRoot)
            flash("已创建检查点。")
        } catch {
            bannerError = "创建检查点失败：\(error.localizedDescription)"
        }
    }

    func createHandoff(toAgent: AgentKind, targetProfile: String? = nil) async {
        do {
            _ = try await client.createHandoff(
                repoRoot: activeRepoRoot,
                fromAgent: selectedAgent.rawValue,
                toAgent: toAgent.rawValue,
                goalHint: selectedConversation?.summary,
                targetProfile: targetProfile
            )
            await loadRepoMemory(repoRoot: activeRepoRoot)
            flash("已创建发往 \(toAgent.label) 的交接包。")
        } catch {
            bannerError = "创建交接包失败：\(error.localizedDescription)"
        }
    }

    func consumeHandoff(_ handoff: HandoffPacket) async {
        do {
            try await client.markHandoffConsumed(handoffID: handoff.handoffID)
            await loadRepoMemory(repoRoot: activeRepoRoot)
            flash("交接包已标记为已消费。")
        } catch {
            bannerError = "更新交接状态失败：\(error.localizedDescription)"
        }
    }

    func showUserError(_ message: String) {
        bannerError = message
    }

    private func capture<T>(
        _ operation: @escaping @MainActor () async throws -> T
    ) async -> Result<T, Error> {
        do {
            return .success(try await operation())
        } catch {
            return .failure(error)
        }
    }

    /// Set the active repo and reload its memory surfaces + health.
    func setActiveRepo(_ repoRoot: String) async {
        await MainActor.run { self.activeRepoRoot = repoRoot }
        await loadRepoMemory(repoRoot: repoRoot)
        await refreshRepoHealth(repoRoot: repoRoot)
    }

    /// Load trash records (global, not repo-scoped).
    func loadTrashed() async {
        do {
            let list = try await client.listTrashedConversations()
            await MainActor.run { self.trashed = list }
        } catch {
            await MainActor.run { self.trashed = [] }
        }
    }

    /// Refresh the list of repos that have pending candidates. Used by the
    /// Review page's repo picker.
    func refreshReposWithCandidates() async {
        do {
            let repos = try await client.listReposWithCandidates()
            await MainActor.run { self.reposWithCandidates = repos }
        } catch {
            await MainActor.run { self.reposWithCandidates = [] }
        }
    }

    // MARK: - Phase C: sync actions (local folder + WebDAV)

    /// Run local folder sync (OneDrive/Google Drive/Dropbox). Reloads all
    /// agent conversations afterwards so the UI reflects downloaded items.
    func syncLocalNow(folder: String) async {
        guard !syncInProgress else { return }
        syncInProgress = true
        syncStatusKind = nil
        syncStatusMessage = "正在同步本地文件夹…"
        bannerError = nil
        bannerMessage = "正在同步本地文件夹…"
        defer { syncInProgress = false }
        do {
            let result = try await client.syncLocalNow(folder: folder)
            let uploaded = result["uploaded"] as? Int ?? 0
            let downloaded = result["downloaded"] as? Int ?? 0
            let message = "本地同步完成：上传 \(uploaded)，下载 \(downloaded)。"
            syncStatusKind = .success
            syncStatusMessage = message
            flash(message)
            // Reload all agents to show downloaded conversations.
            for kind in sources.compactMap(\.agentKind) {
                conversations[kind] = nil
                await loadConversations(for: kind)
            }
        } catch {
            let message = "本地同步失败：\(error.localizedDescription)"
            syncStatusKind = .failure
            syncStatusMessage = message
            bannerError = message
        }
    }

    /// Verify a WebDAV server connection. Returns true if reachable.
    @discardableResult
    func verifyWebDAV(
        scheme: String? = nil,
        host: String,
        path: String,
        remotePath: String? = nil,
        username: String?,
        password: String?
    ) async -> Bool {
        do {
            let result = try await client.verifyWebDAVServer(
                scheme: scheme,
                host: host,
                path: path,
                remotePath: remotePath,
                username: username,
                password: password
            )
            let ok = result["ok"] as? Bool ?? false
            if ok {
                flash("WebDAV 服务器连接成功。")
            } else {
                let status = result["status"] as? Int ?? 0
                bannerError = "WebDAV 连接失败（HTTP \(status)）。"
            }
            return ok
        } catch {
            bannerError = "WebDAV 验证失败：\(error.localizedDescription)"
            return false
        }
    }

    /// Run a WebDAV sync now. Uploads all local conversations to the server
    /// and returns stable feedback for the settings surface. The top banner is
    /// still updated for workbench/status-item callers.
    @discardableResult
    func syncWebDAVNow(
        scheme: String? = nil,
        host: String,
        path: String,
        remotePath: String? = nil,
        username: String?,
        password: String?
    ) async -> WebDAVSyncFeedback {
        guard !syncInProgress else {
            return WebDAVSyncFeedback(
                kind: .warning,
                message: syncStatusMessage ?? "已有同步任务正在进行。"
            )
        }
        syncInProgress = true
        syncStatusKind = nil
        syncStatusMessage = "正在连接 WebDAV…"
        bannerError = nil
        bannerMessage = "正在连接 WebDAV…"
        defer { syncInProgress = false }
        do {
            let result = try await client.syncWebDAVNow(
                scheme: scheme,
                host: host,
                path: path,
                remotePath: remotePath,
                username: username,
                password: password,
                progress: { [weak self] progress in
                    await MainActor.run {
                        guard let self else { return }
                        let message: String
                        if progress.uploadingManifest {
                            message = "正在更新 WebDAV 增量清单…"
                        } else if progress.completedCount == 0 {
                            message = "WebDAV 已连接，正在比较 \(progress.totalCount) 条记录…"
                        } else {
                            message = "WebDAV 增量同步：已比较 \(progress.completedCount)/\(progress.totalCount)，上传 \(progress.uploadedCount)，下载 \(progress.downloadedCount)，跳过 \(progress.skippedCount)。"
                        }
                        self.syncStatusMessage = message
                        self.bannerMessage = message
                    }
                }
            )
            let uploaded = result["uploaded_count"] as? Int ?? 0
            let downloaded = result["downloaded_count"] as? Int ?? 0
            let skipped = result["skipped_count"] as? Int ?? 0
            let total = result["total_count"] as? Int ?? 0
            let errors = result["errors"] as? [String] ?? []
            if errors.isEmpty {
                let message = "WebDAV 增量同步完成：共 \(total) 条，上传 \(uploaded)，下载 \(downloaded)，未变化 \(skipped)。"
                syncStatusKind = .success
                syncStatusMessage = message
                flash(message)
                return WebDAVSyncFeedback(kind: .success, message: message)
            } else {
                let message = "WebDAV 增量同步完成但有错误：上传 \(uploaded)，下载 \(downloaded)，跳过 \(skipped)，\(errors.count) 条失败。"
                syncStatusKind = .warning
                syncStatusMessage = message
                flash(message)
                return WebDAVSyncFeedback(kind: .warning, message: message)
            }
        } catch {
            let message = "WebDAV 同步失败：\(error.localizedDescription)"
            syncStatusKind = .failure
            syncStatusMessage = message
            bannerError = message
            return WebDAVSyncFeedback(kind: .failure, message: message)
        }
    }

    /// Save WebDAV password to the AI Memory keychain (independent service).
    func saveWebDAVPassword(username: String, password: String) async {
        do {
            try await client.saveWebDAVPassword(username: username, password: password)
            flash("WebDAV 密码已保存到钥匙串。")
        } catch {
            bannerError = "密码保存失败：\(error.localizedDescription)"
        }
    }

    /// Returns the effective sync config from settings.json. nil if unconfigured.
    func currentSyncConfig() -> (
        folder: String,
        webdavScheme: String,
        webdavHost: String,
        webdavPath: String,
        remotePath: String,
        webdavUser: String
    )? {
        guard let settings = appSettings, let sync = settings["sync"] as? [String: Any] else { return nil }
        let folder = (sync["syncFolder"] as? String) ?? (sync["sync_folder"] as? String) ?? ""
        let host = (sync["webdavHost"] as? String) ?? (sync["webdav_host"] as? String) ?? ""
        let path = (sync["webdavPath"] as? String) ?? (sync["webdav_path"] as? String) ?? ""
        let scheme = (sync["webdavScheme"] as? String)
            ?? (sync["webdav_scheme"] as? String) ?? "https"
        let remotePath = (sync["remotePath"] as? String)
            ?? (sync["remote_path"] as? String) ?? "chatmem"
        let user = (sync["username"] as? String) ?? (sync["webdav_username"] as? String) ?? ""
        return (folder, scheme, host, path, remotePath, user)
    }

    /// Load app settings into cache. Used by bootstrap + Settings page.
    func loadAppSettings() async {
        do {
            let preferences = try await settingsStore.load()
            let settings = try Self.dictionary(from: preferences)
            appSettings = settings
            let font = (settings["fontFamily"] as? String)
                ?? (settings["font_family"] as? String)
            if let family = font.flatMap(Theme.FontFamily.init(rawValue:)) {
                Theme.applyFont(family)
            }
            configureAutomaticBackup(using: preferences)
            NotificationCenter.default.post(
                name: .interfaceLocaleDidChange,
                object: preferences.locale
            )
        } catch {
            appSettings = nil
            bannerError = "加载设置失败：\(error.localizedDescription)"
        }
    }

    /// Imports ChatMem's existing WebDAV endpoint and credential once, without
    /// modifying ChatMem or overwriting a WebDAV endpoint already configured in
    /// AI Memory. ChatMem's local sync folder is intentionally not imported:
    /// the workbench action gives local-folder sync priority over WebDAV.
    @discardableResult
    private func importChatMemWebDAVIfNeeded() async -> Bool {
        guard let sourceURL = DataPaths.chatMemSettingsURL else { return false }

        do {
            var target = try await settingsStore.load()
            let sourceData = try Data(contentsOf: sourceURL)
            let source = try JSONDecoder().decode(AppPreferences.self, from: sourceData)
            let sourceSync = source.sync
            let sourceHost = sourceSync.webdavHost.trimmingCharacters(
                in: .whitespacesAndNewlines
            )
            guard !sourceHost.isEmpty else { return false }

            let targetHost = target.sync.webdavHost.trimmingCharacters(
                in: .whitespacesAndNewlines
            )
            let endpointNeedsImport = targetHost.isEmpty
            let endpointMatchesChatMem = targetHost == sourceHost
                && target.sync.username.trimmingCharacters(
                    in: .whitespacesAndNewlines
                ) == sourceSync.username.trimmingCharacters(
                    in: .whitespacesAndNewlines
                )
            guard endpointNeedsImport || endpointMatchesChatMem else { return false }

            if endpointNeedsImport {
                target.sync.provider = "webdav"
                target.sync.webdavScheme = sourceSync.webdavScheme
                target.sync.webdavHost = sourceSync.webdavHost
                target.sync.webdavPath = sourceSync.webdavPath
                target.sync.username = sourceSync.username
                target.sync.remotePath = sourceSync.remotePath
                target.sync.downloadMode = sourceSync.downloadMode
                try await settingsStore.save(target)
            }

            let username = sourceSync.username.trimmingCharacters(
                in: .whitespacesAndNewlines
            )
            guard !username.isEmpty else {
                if endpointNeedsImport {
                    bannerError = "已导入 ChatMem 的 WebDAV 地址，但原配置没有用户名。"
                    telemetry.lifecycle("ChatMem WebDAV endpoint imported without username")
                }
                return endpointNeedsImport
            }

            if let existing = try await client.loadWebDAVPassword(username: username),
               !existing.isEmpty {
                if endpointNeedsImport {
                    flash("已从 ChatMem 导入 WebDAV 配置。")
                }
                return endpointNeedsImport
            }

            let sourceCredentials = NativeCredentialStore(
                service: DataPaths.chatMemKeychainService
            )
            let keychainPassword = try await sourceCredentials.load(account: username)
            let settingsPassword = Self.chatMemWebDAVPassword(from: sourceData)
            if let password = [keychainPassword, settingsPassword]
                .compactMap({ $0 })
                .first(where: { !$0.isEmpty }) {
                try await client.saveWebDAVPassword(
                    username: username,
                    password: password
                )
                flash("已从 ChatMem 导入 WebDAV 配置。")
                telemetry.lifecycle("ChatMem WebDAV settings and credential imported")
            } else {
                bannerError = "已导入 ChatMem 的 WebDAV 地址；原钥匙串中未找到密码，请在设置中补充。"
                telemetry.lifecycle("ChatMem WebDAV endpoint imported without credential")
            }
            return true
        } catch {
            bannerError = "导入 ChatMem WebDAV 配置失败：\(error.localizedDescription)"
            telemetry.bridgeError("ChatMem WebDAV import failed: \(error)")
            return false
        }
    }

    /// ChatMem deliberately keeps a settings-file credential fallback for
    /// ad-hoc signed builds whose Keychain identity changes after an update.
    /// Read that established fallback without adding the secret to AI Memory's
    /// settings file; it is written only to AI Memory's Keychain service.
    private static func chatMemWebDAVPassword(from data: Data) -> String? {
        guard let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let sync = root["sync"] as? [String: Any],
              let password = sync["password"] as? String,
              !password.isEmpty else {
            return nil
        }
        return password
    }

    /// Performs the configured launch check once per process. An unavailable
    /// update server never blocks startup; only a real newer release is shown.
    private func checkForUpdatesAtLaunchIfNeeded() async {
        guard !didRunAutomaticUpdateCheck else { return }
        didRunAutomaticUpdateCheck = true
        guard let settings = try? await settingsStore.load(),
              settings.autoCheckUpdates,
              let feedURL = URL(
                string: settings.updateFeedURL.trimmingCharacters(
                    in: .whitespacesAndNewlines
                )
              ),
              !settings.updateFeedURL.isEmpty
        else { return }

        let currentVersion = Bundle.main.object(
            forInfoDictionaryKey: "CFBundleShortVersionString"
        ) as? String ?? "0"
        do {
            if case .available(let release) = try await updateService.check(
                feedURL: feedURL,
                currentVersion: currentVersion
            ) {
                availableUpdate = release
                telemetry.lifecycle("update available: \(release.version)")
            }
        } catch {
            telemetry.lifecycle("automatic update check skipped: \(error.localizedDescription)")
        }
    }

    /// Downloads the pending release and installs it over the running bundle,
    /// then relaunches. Falls back to opening the DMG when this copy is not the
    /// writable /Applications install.
    func installAvailableUpdate() async {
        guard let release = availableUpdate, !updateInstalling else { return }
        updateInstalling = true
        updateProgress = 0
        updateStage = "正在下载 \(release.version)…"
        defer { updateInstalling = false }

        let installer = NativeUpdateInstaller.shared
        do {
            let dmgURL = try await installer.download(release) { [weak self] fraction in
                Task { @MainActor in
                    self?.updateProgress = fraction
                    self?.updateStage = "正在下载 \(release.version)… \(Int(fraction * 100))%"
                }
            }
            updateStage = "正在校验签名并安装…"
            let outcome = try await Task.detached(priority: .userInitiated) {
                try installer.install(from: dmgURL)
            }.value

            switch outcome {
            case .installed(let appURL, let rollbackURL):
                updateStage = "已安装 \(release.version)，正在重启…"
                availableUpdate = nil
                telemetry.lifecycle("update installed: \(release.version), relaunching")
                try installer.relaunch(appURL: appURL, rollbackURL: rollbackURL)
            case .openedInstaller(let url):
                updateStage = nil
                bannerError = """
                当前运行的不是 /Applications/AIMemory.app，无法就地覆盖。\
                已打开 \(url.lastPathComponent)，请手动拖入「应用程序」。
                """
            }
        } catch {
            updateStage = nil
            bannerError = "更新失败：\(error.localizedDescription)"
            telemetry.lifecycle("update install failed: \(error.localizedDescription)")
        }
    }

    /// Persist the font family choice to settings.json + apply immediately.
    func setFontFamily(_ family: Theme.FontFamily) async {
        var current = appSettings ?? [:]
        current["fontFamily"] = family.rawValue
        current.removeValue(forKey: "font_family")
        do {
            let preferences = try Self.preferences(from: current)
            try await settingsStore.save(preferences)
            appSettings = try Self.dictionary(from: preferences)
            Theme.applyFont(family)
        } catch {
            bannerError = "保存字体设置失败：\(error.localizedDescription)"
        }
    }

    /// Persist the locale choice to settings.json.
    func setLocale(_ locale: String) async {
        var current = appSettings ?? [:]
        current["locale"] = locale
        do {
            let preferences = try Self.preferences(from: current)
            try await settingsStore.save(preferences)
            appSettings = try Self.dictionary(from: preferences)
            NotificationCenter.default.post(
                name: .interfaceLocaleDidChange,
                object: locale
            )
        } catch {
            bannerError = "保存语言设置失败：\(error.localizedDescription)"
        }
    }

    var trashRetentionDays: Int {
        let value = (appSettings?["trashRetentionDays"] as? Int)
            ?? (appSettings?["trash_retention_days"] as? Int)
            ?? 14
        return min(365, max(1, value))
    }

    func setTrashRetentionDays(_ days: Int) async {
        var current = appSettings ?? [:]
        current["trashRetentionDays"] = min(365, max(1, days))
        current.removeValue(forKey: "trash_retention_days")
        do {
            _ = try await saveSettingsDictionary(current)
        } catch {
            bannerError = "保存回收站保留天数失败：\(error.localizedDescription)"
        }
    }

    // MARK: - Favorites

    /// True if the given conversation id is favorited.
    func isFavorite(_ conversationID: String) -> Bool {
        let favorites = favoriteSnapshots
        return favorites[Self.favoriteKey(agent: selectedAgent.rawValue, id: conversationID)] != nil
            || favorites[conversationID] != nil
    }

    /// All favorited conversation ids.
    var favoriteIDs: Set<String> {
        Set(favoriteSnapshots.values.compactMap { rawValue in
            (rawValue as? [String: Any])?["id"] as? String
        })
    }

    /// Favorited ConversationSummary rows (from currently-loaded conversations
    /// across all agents, plus any favorited id even if not loaded).
    var favoriteConversations: [ConversationSummary] {
        let favorites = favoriteSnapshots
        var seen = Set<String>()
        var result: [ConversationSummary] = []

        for list in conversations.values {
            for conv in list {
                let key = Self.favoriteKey(agent: conv.sourceAgent, id: conv.id)
                guard favorites[key] != nil || favorites[conv.id] != nil,
                      !seen.contains(key) else { continue }
                result.append(conv)
                seen.insert(key)
            }
        }

        for rawValue in favorites.values {
            guard let snapshot = rawValue as? [String: Any],
                  let id = snapshot["id"] as? String else { continue }
            let sourceAgent = (snapshot["sourceAgent"] as? String)
                ?? (snapshot["source_agent"] as? String)
                ?? selectedAgent.rawValue
            let key = Self.favoriteKey(agent: sourceAgent, id: id)
            guard !seen.contains(key) else { continue }
            let title = (snapshot["title"] as? String)
                ?? (snapshot["summary"] as? String)
                ?? id
            result.append(ConversationSummary(
                id: id,
                sourceAgent: sourceAgent,
                projectDir: (snapshot["projectDir"] as? String)
                    ?? (snapshot["project_dir"] as? String)
                    ?? "",
                createdAt: (snapshot["createdAt"] as? String)
                    ?? (snapshot["created_at"] as? String)
                    ?? "",
                updatedAt: (snapshot["updatedAt"] as? String)
                    ?? (snapshot["updated_at"] as? String)
                    ?? "",
                summary: title,
                messageCount: 0,
                fileCount: 0
            ))
            seen.insert(key)
        }
        return result.sorted { left, right in
            let leftPinned = favoriteSnapshot(
                conversationID: left.id,
                agent: left.agentKind
            )?.pinned ?? false
            let rightPinned = favoriteSnapshot(
                conversationID: right.id,
                agent: right.agentKind
            )?.pinned ?? false
            if leftPinned != rightPinned { return leftPinned }
            let useCreated = sortMode == .createdDesc
            return useCreated
                ? left.createdAt > right.createdAt
                : left.updatedAt > right.updatedAt
        }
    }

    /// Toggle favorite for a conversation. Persists to settings.json.
    /// Toggle favorite. Synchronous (safe from button actions).
    func toggleFavorite(_ conversationID: String, agent: AgentKind? = nil) {
        var favorites = favoriteSnapshots
        let sourceAgent = agent ?? selectedAgent
        let key = Self.favoriteKey(agent: sourceAgent.rawValue, id: conversationID)
        if favorites[key] != nil || favorites[conversationID] != nil {
            favorites.removeValue(forKey: key)
            favorites.removeValue(forKey: conversationID)
        } else {
            let summary = conversations[sourceAgent]?.first { $0.id == conversationID }
            favorites[key] = [
                "id": conversationID,
                "sourceAgent": sourceAgent.rawValue,
                "projectDir": summary?.projectDir ?? "",
                "title": summary?.displayTitle ?? conversationID,
                "createdAt": summary?.createdAt ?? "",
                "updatedAt": summary?.updatedAt ?? "",
                "note": "",
                "tags": [],
                "pinned": false,
            ]
        }
        persistFavorites(favorites)
    }

    func favoriteSnapshot(
        conversationID: String,
        agent: AgentKind? = nil
    ) -> FavoriteConversationSnapshot? {
        let sourceAgent = agent ?? selectedAgent
        let raw = favoriteSnapshots[
            Self.favoriteKey(agent: sourceAgent.rawValue, id: conversationID)
        ] ?? favoriteSnapshots[conversationID]
        guard let dictionary = raw as? [String: Any],
              let data = try? JSONSerialization.data(withJSONObject: dictionary)
        else { return nil }
        return try? JSONDecoder().decode(FavoriteConversationSnapshot.self, from: data)
    }

    func updateFavorite(
        conversationID: String,
        agent: AgentKind,
        note: String? = nil,
        tags: [String]? = nil,
        pinned: Bool? = nil
    ) {
        let key = Self.favoriteKey(agent: agent.rawValue, id: conversationID)
        var favorites = favoriteSnapshots
        guard var snapshot = favoriteSnapshot(
            conversationID: conversationID,
            agent: agent
        ) else { return }
        if let note { snapshot.note = note }
        if let tags {
            snapshot.tags = Array(
                Set(
                    tags.map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
                        .filter { !$0.isEmpty }
                )
            ).sorted()
        }
        if let pinned { snapshot.pinned = pinned }
        guard let data = try? JSONEncoder().encode(snapshot),
              let object = try? JSONSerialization.jsonObject(with: data)
        else {
            bannerError = "收藏更新失败：无法编码收藏元数据。"
            return
        }
        favorites.removeValue(forKey: conversationID)
        favorites[key] = object
        persistFavorites(favorites)
    }

    func favoriteContinuationCard(for conversation: ConversationSummary) -> String {
        let favorite = favoriteSnapshot(
            conversationID: conversation.id,
            agent: conversation.agentKind
        )
        var lines = [
            "# Favorite Continuation Card",
            "",
            "title: \(conversation.displayTitle)",
            "source: \(conversation.sourceAgent)",
            "conversation: \(conversation.id)",
            "project: \(conversation.projectDir.isEmpty ? "--" : conversation.projectDir)",
            "updated: \(conversation.updatedAt)",
        ]
        if favorite?.pinned == true { lines.append("priority: pinned") }
        if let tags = favorite?.tags, !tags.isEmpty {
            lines.append("tags: \(tags.joined(separator: ", "))")
        }
        if let note = favorite?.note.trimmingCharacters(
            in: .whitespacesAndNewlines
        ), !note.isEmpty {
            lines.append("note: \(note)")
        }
        lines += [
            "",
            "Use ChatMem to reopen this favorite, load the source-backed conversation, and continue from the latest useful state instead of rereading unrelated history.",
        ]
        return lines.joined(separator: "\n")
    }

    func continuationBrief(for detail: ConversationDetail) -> String {
        ContinuationBriefBuilder.build(
            repoRoot: detail.projectDir.isEmpty ? activeRepoRoot : detail.projectDir,
            conversation: detail
        )
    }

    private func persistFavorites(_ favorites: [String: Any]) {
        var current = appSettings ?? [:]
        current["favoriteConversations"] = favorites
        current.removeValue(forKey: "favorite_conversations")
        appSettings = current
        let preferences: AppPreferences
        do {
            preferences = try Self.preferences(from: current)
        } catch {
            bannerError = "收藏失败：\(error.localizedDescription)"
            return
        }
        let store = settingsStore
        Task {
            do {
                try await store.save(preferences)
                self.appSettings = try Self.dictionary(from: preferences)
            } catch {
                self.bannerError = "收藏失败：\(error.localizedDescription)"
            }
        }
    }

    private var favoriteSnapshots: [String: Any] {
        (appSettings?["favoriteConversations"] as? [String: Any])
            ?? (appSettings?["favorite_conversations"] as? [String: Any])
            ?? [:]
    }

    private static func favoriteKey(agent: String, id: String) -> String {
        "\(agent):\(id)"
    }

    /// Replace the cached app settings (used by SettingsView after saving).
    func setAppSettings(_ value: [String: Any]?) {
        appSettings = value
        if let id = selectedConversationID {
            scheduleAutomaticCapture(agent: selectedAgent, id: id)
        } else {
            automaticCaptureTask?.cancel()
            automaticCaptureTask = nil
        }
    }

    func loadSettingsDictionary() async throws -> [String: Any] {
        try Self.dictionary(from: await settingsStore.load())
    }

    @discardableResult
    func saveSettingsDictionary(_ value: [String: Any]) async throws -> [String: Any] {
        let preferences = try Self.preferences(from: value)
        try await settingsStore.save(preferences)
        let normalized = try Self.dictionary(from: preferences)
        appSettings = normalized
        configureAutomaticBackup(using: preferences)
        return normalized
    }

    func createRecoveryPoint(reason: String = "manual") async {
        do {
            let result = try await backupService.createRecoveryPoint(reason: reason)
            if result.created {
                flash("已创建增量恢复点：\(result.url.lastPathComponent)")
            } else {
                flash("数据没有变化，已保留现有恢复点。")
            }
        } catch {
            bannerError = error.localizedDescription
        }
    }

    private func configureAutomaticBackup(using preferences: AppPreferences) {
        automaticBackupTask?.cancel()
        automaticBackupTask = nil
        guard preferences.autoBackupEnabled else { return }
        let interval = preferences.autoBackupIntervalMinutes
        automaticBackupTask = Task { [weak self] in
            while !Task.isCancelled {
                do {
                    try await Task.sleep(for: .seconds(interval * 60))
                    guard !Task.isCancelled, let self else { return }
                    _ = try await self.backupService.createRecoveryPoint(
                        reason: "automatic"
                    )
                } catch is CancellationError {
                    return
                } catch {
                    guard let self else { return }
                    self.bannerError = "自动备份失败：\(error.localizedDescription)"
                }
            }
        }
    }

    private static func preferences(from dictionary: [String: Any]) throws -> AppPreferences {
        let data = try JSONSerialization.data(withJSONObject: dictionary)
        var preferences = try JSONDecoder().decode(AppPreferences.self, from: data)
        preferences.schemaVersion = AppPreferences.schemaVersion
        preferences.normalize()
        return preferences
    }

    private static func dictionary(from preferences: AppPreferences) throws -> [String: Any] {
        let data = try JSONEncoder().encode(preferences)
        guard let dictionary = try JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            throw NativeSettingsError.invalidRoot
        }
        return dictionary
    }

    /// The workbench "立即同步" entry: runs whichever sync is configured.
    /// Priority: local folder > WebDAV. Reports nothing-configured if neither.
    func syncNow() async {
        let cfg = currentSyncConfig()
        if let cfg, !cfg.folder.isEmpty {
            await syncLocalNow(folder: cfg.folder)
        } else if let cfg, !cfg.webdavHost.isEmpty {
            let pw = (try? await client.loadWebDAVPassword(username: cfg.webdavUser)) ?? nil
            await syncWebDAVNow(
                scheme: cfg.webdavScheme,
                host: cfg.webdavHost,
                path: cfg.webdavPath,
                remotePath: cfg.remotePath,
                username: cfg.webdavUser,
                password: pw
            )
        } else {
            let message = "未配置同步。请在设置页配置 WebDAV 或本地同步文件夹。"
            syncStatusKind = .failure
            syncStatusMessage = message
            bannerError = message
        }
    }

    /// On bootstrap, auto-select a repo with pending candidates so the Review
    /// page isn't empty. Falls back to the home directory.
    func autoSelectActiveRepo() async {
        await refreshReposWithCandidates()
        if let first = reposWithCandidates.first {
            await MainActor.run { self.activeRepoRoot = first.repoRoot }
        }
    }

    // MARK: - Phase C: trash write actions

    func trashConversation(agent: String, id: String) async {
        do {
            let retention = (appSettings?["trashRetentionDays"] as? Int)
                ?? (appSettings?["trash_retention_days"] as? Int)
                ?? 14
            let result = try await client.trashConversation(
                agent: agent,
                id: id,
                retentionDays: retention
            )
            await reloadCurrentAgent()
            await loadTrashed()
            if result.warnings.isEmpty {
                flash("已移入回收站。")
            } else {
                flash("已移入回收站。\(result.warnings.joined(separator: "；"))")
            }
        } catch {
            bannerError = "移入回收站失败：\(error.localizedDescription)"
        }
    }

    func toggleBulkSelection(_ conversation: ConversationSummary) {
        let key = Self.detailKey(agent: conversation.sourceAgent, id: conversation.id)
        if selectedConversationKeys.contains(key) {
            selectedConversationKeys.remove(key)
        } else {
            selectedConversationKeys.insert(key)
        }
    }

    func isBulkSelected(_ conversation: ConversationSummary) -> Bool {
        selectedConversationKeys.contains(
            Self.detailKey(agent: conversation.sourceAgent, id: conversation.id)
        )
    }

    func cancelBulkSelection() {
        bulkSelectionMode = false
        selectedConversationKeys.removeAll()
    }

    func trashBulkSelection() async {
        let targets = currentConversations.filter(isBulkSelected)
        guard !targets.isEmpty else { return }
        let retention = trashRetentionDays
        var failed: [String] = []
        var warnings: [String] = []
        for target in targets {
            do {
                let result = try await client.trashConversation(
                    agent: target.sourceAgent,
                    id: target.id,
                    retentionDays: retention
                )
                warnings.append(contentsOf: result.warnings)
            } catch {
                failed.append(target.displayTitle)
            }
        }
        cancelBulkSelection()
        await reloadCurrentAgent()
        await loadTrashed()
        if failed.isEmpty {
            let warningSuffix = warnings.isEmpty
                ? ""
                : " \(Set(warnings).sorted().joined(separator: "；"))"
            flash("已将 \(targets.count) 条对话移入回收站。\(warningSuffix)")
        } else {
            bannerError = "批量处理完成，\(failed.count) 条失败：\(failed.prefix(3).joined(separator: "、"))"
        }
    }

    func restoreTrashed(trashID: String, agent: String) async {
        do {
            _ = try await client.restoreTrashed(trashID: trashID, agent: agent)
            await loadTrashed()
            await reloadCurrentAgent()
            flash("已恢复对话。")
        } catch {
            bannerError = "恢复失败：\(error.localizedDescription)"
        }
    }

    func deleteTrashRecord(trashID: String, agent: String) async {
        do {
            _ = try await client.deleteTrashRecord(trashID: trashID, agent: agent)
            await loadTrashed()
            flash("已永久删除。")
        } catch {
            bannerError = "删除失败：\(error.localizedDescription)"
        }
    }

    func emptyTrash() async {
        do {
            _ = try await client.emptyTrash()
            await loadTrashed()
            flash("回收站已清空。")
        } catch {
            bannerError = "清空失败：\(error.localizedDescription)"
        }
    }

    // MARK: - Phase C: migrate

    func migrateConversation(source: String, target: String, id: String, mode: String) async {
        do {
            let result = try await client.migrateConversation(
                source: source, target: target, id: id, mode: mode
            )
            if result.verified {
                flash("迁移成功：\(source) → \(target)，新 id \(result.newID.prefix(8))…")
            } else {
                bannerError = "迁移未通过验证，请检查目标 agent。"
            }
            if mode == "cut" { await reloadCurrentAgent() }
        } catch {
            bannerError = "迁移失败：\(error.localizedDescription)"
        }
    }

    /// Set repo health directly (used by LocalHistoryView after a scan).
    func setRepoHealth(_ health: RepoHealth) {
        repoHealth = health
    }

    // MARK: - Phase C write actions (memory governance)

    /// Approve a candidate as a new startup rule. Reloads memory data after.
    func approveCandidate(
        _ candidate: MemoryCandidate,
        title: String? = nil,
        value: String? = nil,
        usageHint: String? = nil
    ) async {
        do {
            let edited = title != nil || value != nil || usageHint != nil
            try await client.reviewMemoryCandidate(
                candidateID: candidate.candidateID,
                action: edited ? "approve_with_edit" : "approve",
                title: title ?? candidate.summary,
                value: value ?? candidate.value,
                usageHint: usageHint ?? ""
            )
            await loadRepoMemory(repoRoot: activeRepoRoot)
            flash("已批准为启动规则。")
        } catch {
            bannerError = "批准失败：\(error.localizedDescription)"
        }
    }

    func rejectAllPendingCandidates() async {
        let pending = pendingCandidates
        guard !pending.isEmpty else { return }
        var failed = 0
        for candidate in pending {
            do {
                try await client.reviewMemoryCandidate(
                    candidateID: candidate.candidateID,
                    action: "reject"
                )
            } catch {
                failed += 1
            }
        }
        await loadRepoMemory(repoRoot: activeRepoRoot)
        if failed == 0 {
            flash("已忽略 \(pending.count) 条候选。")
        } else {
            bannerError = "已处理 \(pending.count - failed) 条，\(failed) 条忽略失败。"
        }
    }

    func rejectCandidate(_ candidate: MemoryCandidate) async {
        do {
            try await client.reviewMemoryCandidate(
                candidateID: candidate.candidateID, action: "reject"
            )
            await loadRepoMemory(repoRoot: activeRepoRoot)
            flash("已拒绝候选。")
        } catch {
            bannerError = "拒绝失败：\(error.localizedDescription)"
        }
    }

    func snoozeCandidate(_ candidate: MemoryCandidate) async {
        do {
            try await client.reviewMemoryCandidate(
                candidateID: candidate.candidateID, action: "snooze"
            )
            await loadRepoMemory(repoRoot: activeRepoRoot)
            flash("已暂缓候选。")
        } catch {
            bannerError = "暂缓失败：\(error.localizedDescription)"
        }
    }

    func retireMemory(_ memory: ApprovedMemory) async {
        do {
            try await client.retireMemory(memoryID: memory.memoryID)
            await loadRepoMemory(repoRoot: activeRepoRoot)
            flash("已停用规则。")
        } catch {
            bannerError = "停用失败：\(error.localizedDescription)"
        }
    }

    func reverifyMemory(_ memory: ApprovedMemory) async {
        do {
            try await client.reverifyMemory(memoryID: memory.memoryID)
            await loadRepoMemory(repoRoot: activeRepoRoot)
            flash("已重新核验。")
        } catch {
            bannerError = "核验失败：\(error.localizedDescription)"
        }
    }

    // MARK: - Phase C: rebuilds

    func rebuildWiki() async {
        do {
            let pages = try await client.rebuildRepoWiki(repoRoot: activeRepoRoot)
            await MainActor.run { self.wikiPages = pages }
            flash("已重建 Wiki（\(pages.count) 页）。")
        } catch {
            bannerError = "重建 Wiki 失败：\(error.localizedDescription)"
        }
    }

    func rebuildEmbeddings() async {
        do {
            _ = try await client.rebuildRepoEmbeddings(repoRoot: activeRepoRoot)
            flash("已重建向量索引。")
        } catch {
            bannerError = "重建向量失败：\(error.localizedDescription)"
        }
    }

    func mergeAlias(aliasRoot: String) async {
        do {
            _ = try await client.mergeRepoAlias(repoRoot: activeRepoRoot, aliasRoot: aliasRoot)
            await loadRepoMemory(repoRoot: activeRepoRoot)
            await refreshRepoHealth(repoRoot: activeRepoRoot)
            flash("已合并别名。")
        } catch {
            bannerError = "合并别名失败：\(error.localizedDescription)"
        }
    }

    /// Clears cached conversations for an agent so the next load re-fetches.
    func clearConversations(for agent: AgentKind) {
        conversations[agent] = nil
    }

    /// Loads conversations for every detected available source. Used by the
    /// workbench "load all" action so the cross-agent count is accurate.
    func loadAllAgentConversations() async {
        guard !syncInProgress else { return }
        syncInProgress = true
        loading = .loading("正在同步全部本机 agent 记录…")
        defer { syncInProgress = false }
        do {
            let syncReport = try await client.synchronizeInstalledAgentHistory()
            let installedAgents = applyInstalledHistorySync(syncReport)
            for kind in installedAgents {
                conversations[kind] = nil
                await loadConversations(for: kind)
            }
            loading = .ready
        } catch {
            loading = .failed(error.localizedDescription)
            bannerError = "同步本机 agent 记录失败：\(error.localizedDescription)"
        }
    }

    /// Applies the post-import source snapshot and publishes one persistent
    /// workbench status. Returning typed kinds keeps bootstrap and the manual
    /// fallback entry point on the exact same behavior path.
    @discardableResult
    private func applyInstalledHistorySync(
        _ report: NativeInstalledHistorySyncReport
    ) -> [AgentKind] {
        let installed = report.availableAgents.compactMap(AgentKind.init(rawValue:))
        sources = installed.map {
            ConversationSourceStatus(
                agent: $0.rawValue,
                label: $0.label,
                available: true
            )
        }
        if report.warnings.isEmpty {
            syncStatusKind = .success
            syncStatusMessage = "启动自动同步完成：\(installed.count) 个本机来源，本次扫描 \(report.total) 条对话。"
        } else {
            syncStatusKind = .warning
            syncStatusMessage = "启动自动同步完成：\(installed.count) 个本机来源，本次扫描 \(report.total) 条对话，\(report.warnings.count) 项需检查。"
        }
        return installed
    }

    /// Opens the real source conversation behind a run, artifact, or episode.
    /// Historical projections may store either AI Memory's internal id or the
    /// original agent id, so resolve through the native read path instead of
    /// assuming the currently selected source owns it.
    func openHistoricalConversation(
        id: String,
        sourceAgent: String? = nil
    ) async {
        let preferred = sourceAgent.flatMap(AgentKind.init(rawValue:))
        let orderedAgents = ([preferred].compactMap { $0 } + AgentKind.allCases)
            .reduce(into: [AgentKind]()) { result, agent in
                if !result.contains(agent) { result.append(agent) }
            }
        let candidateIDs = ([id] + AgentKind.allCases.compactMap { agent in
            let prefix = "\(agent.rawValue):"
            return id.hasPrefix(prefix) ? String(id.dropFirst(prefix.count)) : nil
        }).reduce(into: [String]()) { result, candidate in
            if !candidate.isEmpty, !result.contains(candidate) {
                result.append(candidate)
            }
        }

        for agent in orderedAgents {
            for candidateID in candidateIDs {
                do {
                    _ = try await client.readConversation(
                        agent: agent.rawValue,
                        id: candidateID
                    )
                    selectAgent(agent)
                    selectConversation(candidateID)
                    return
                } catch {
                    continue
                }
            }
        }

        bannerError = "找不到这条历史记录对应的原始对话；源记录可能已被移除。"
    }

    /// Reloads conversations for every detected source agent. Used after
    /// importing from ChatMem so the UI reflects the new data. Also posts a
    /// notification so AppKit observers (toolbar status) refresh.
    func reloadAllAgents() async {
        for kind in sources.compactMap(\.agentKind) {
            conversations[kind] = nil
            await loadConversations(for: kind)
        }
        NotificationCenter.default.post(name: Self.didChangeNotification, object: self)
    }

    /// Notification name used by AppKit views that observe the store without
    /// going through SwiftUI's observation.
    static let didChangeNotification = Notification.Name("com.aimemory.app.storeDidChange")

    // MARK: - Workspace navigation

    func openWorkspace(_ destination: WorkspaceDestination) {
        workspace = destination
    }

    func toggleMemoryDrawer(tab: MemoryDrawerTab? = nil) {
        if let tab { memoryDrawerTab = tab }
        memoryDrawerOpen.toggle()
    }

    func openMemoryDrawer(tab: MemoryDrawerTab) {
        memoryDrawerTab = tab
        memoryDrawerOpen = true
    }

    func setMemoryDrawerTab(_ tab: MemoryDrawerTab) {
        memoryDrawerTab = tab
    }

    func dismissBanner() {
        bannerMessage = nil
        bannerError = nil
    }

    /// Post a transient success/info message. RootView owns the shared
    /// five-second lifetime so every green banner follows the same rule.
    func flash(_ message: String) {
        bannerMessage = message
        bannerError = nil
    }
}

enum WebDAVFeedbackKind: Sendable {
    case success
    case warning
    case failure
}

struct WebDAVSyncFeedback: Sendable {
    let kind: WebDAVFeedbackKind
    let message: String
}
