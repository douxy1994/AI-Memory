import SwiftUI

/// Embedded project-index section for the unified History workspace. It shows
/// bridge-reported repo health and exposes scan/import/alias repair actions.
struct LocalHistoryIndexView: View {
    @ObservedObject var store: AppStore
    @State private var repoRoot = ""
    @State private var scanning = false
    @State private var importing = false

    var body: some View {
        content
        .background(Theme.appBackground)
        .task {
            repoRoot = effectiveActiveRepoRoot
            await store.refreshRepoHealth(repoRoot: repoRoot)
        }
        .onChange(of: store.activeRepoRoot) { _, newValue in
            guard !newValue.isEmpty, newValue != repoRoot else { return }
            repoRoot = newValue
            Task { await store.refreshRepoHealth(repoRoot: newValue) }
        }
    }

    private var content: some View {
        VStack(alignment: .leading, spacing: 16) {
            VStack(alignment: .leading, spacing: 5) {
                Text("本地历史索引")
                    .font(Theme.appFont(size: 16, weight: .semibold))
                Text("扫描本地 agent 历史、检查索引状态并合并仓库别名。")
                    .font(Theme.appFont(size: 12))
                    .foregroundStyle(Theme.secondaryText)
                HStack(spacing: 10) {
                    TextField("仓库根目录", text: $repoRoot)
                        .textFieldStyle(.roundedBorder)
                        .onSubmit {
                            let target = repoRoot.trimmingCharacters(
                                in: .whitespacesAndNewlines
                            )
                            guard !target.isEmpty else { return }
                            Task {
                                await store.refreshRepoHealth(repoRoot: target)
                            }
                        }
                    Button("使用当前项目") {
                        repoRoot = effectiveActiveRepoRoot
                        Task {
                            await store.refreshRepoHealth(repoRoot: repoRoot)
                        }
                    }
                    .buttonStyle(.bordered)
                }
            }
            .surfaceCard()
            healthCard
            actionsCard
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private var effectiveActiveRepoRoot: String {
        let active = store.activeRepoRoot.trimmingCharacters(
            in: .whitespacesAndNewlines
        )
        return active.isEmpty ? FileManager.default.currentDirectoryPath : active
    }

    private var healthCard: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("索引状态").font(Theme.appFont(size: 15, weight: .semibold))
            if let h = store.repoHealth {
                LazyVGrid(columns: [GridItem(.flexible()), GridItem(.flexible())], spacing: 10) {
                    healthTile("扫描对话", h.latestScan?.scannedConversationCount)
                    healthTile("已索引对话", h.latestScan?.linkedConversationCount)
                    healthTile("候选规则", h.pendingCandidateCount)
                    healthTile("已批准记忆", h.approvedMemoryCount)
                }
                if let docs = h.searchDocumentCount {
                    healthTileRow("检索文档数", "\(docs)")
                }
                if let unmatched = h.latestScan?.unmatchedProjectRoots, !unmatched.isEmpty {
                    VStack(alignment: .leading, spacing: 4) {
                        Text("未匹配的 project root (\(unmatched.count))")
                            .font(Theme.appFont(size: 11))
                            .foregroundStyle(Theme.mutedText)
                        ForEach(unmatched.prefix(5), id: \.self) { p in
                            Text("• \(p.sourceAgent ?? "?") → \(p.projectRoot ?? "?") (\(p.conversationCount ?? 0))")
                                .font(Theme.appFont(size: 10, design: .monospaced))
                                .foregroundStyle(Theme.secondaryText)
                                .lineLimit(1)
                        }
                        if unmatched.count > 5 {
                            Text("+ \(unmatched.count - 5) 个…")
                                .font(Theme.appFont(size: 10))
                                .foregroundStyle(Theme.mutedText)
                        }
                    }
                }
            } else {
                Text("未加载。请在上方填入仓库根目录或点击「扫描」。")
                    .font(Theme.appFont(size: 12))
                    .foregroundStyle(Theme.mutedText)
            }
        }
        .card()
    }

    private func healthTileRow(_ label: String, _ value: String) -> some View {
        HStack {
            Text(LocalizedStringKey(label))
                .font(Theme.appFont(size: 11))
                .foregroundStyle(Theme.mutedText)
            Spacer()
            Text(value).font(Theme.appFont(size: 12, weight: .medium))
        }
        .padding(.vertical, 2)
    }

    private var actionsCard: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("操作").font(Theme.appFont(size: 15, weight: .semibold))
            HStack(spacing: 10) {
                actionBtn("扫描", "magnifyingglass", disabled: repoRoot.isEmpty) {
                    await runScan()
                }
                actionBtn("导入全部", "tray.and.arrow.down", disabled: false) {
                    await runImport()
                }
                actionBtn("合并别名", "link", disabled: !hasUnmatchedAlias) {
                    await runMergeFirstAlias()
                }
            }
            HStack(spacing: 10) {
                actionBtn("重建 Wiki", "doc.text.magnifyingglass", disabled: repoRoot.isEmpty) {
                    await store.rebuildWiki()
                }
                actionBtn("重建向量", "circle.hexagongrid", disabled: repoRoot.isEmpty) {
                    await store.rebuildEmbeddings()
                }
            }
        }
        .card()
    }

    /// True if the current repo health lists at least one unmatched project
    /// root that could be merged as an alias.
    private var hasUnmatchedAlias: Bool {
        guard let unmatched = store.repoHealth?.latestScan?.unmatchedProjectRoots else {
            return false
        }
        return !unmatched.isEmpty
    }

    private func runMergeFirstAlias() async {
        guard let unmatched = store.repoHealth?.latestScan?.unmatchedProjectRoots,
              let first = unmatched.first,
              let alias = first.projectRoot else { return }
        await store.mergeAlias(aliasRoot: alias)
    }

    private func actionBtn(_ label: String, _ icon: String, disabled: Bool, action: @escaping () async -> Void) -> some View {
        Button(action: {Task { await action() }}) {
            Label(LocalizedStringKey(label), systemImage: icon)
                .font(Theme.appFont(size: 12))
                .frame(maxWidth: .infinity)
                .padding(.vertical, 8)
        }
        .buttonStyle(.bordered)
        .controlSize(.small)
        .disabled(disabled)
    }

    private func healthTile(_ label: String, _ value: Int?) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(LocalizedStringKey(label))
                .font(Theme.appFont(size: 10))
                .foregroundStyle(Theme.mutedText)
            Text(value.map { "\($0)" } ?? "—")
                .font(Theme.appFont(size: 18, weight: .semibold))
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(10)
        .background(Theme.soft)
        .clipShape(RoundedRectangle(cornerRadius: 6))
    }

    // MARK: - Actions

    private func runScan() async {
        guard !repoRoot.isEmpty else { return }
        scanning = true
        defer { scanning = false }
        do {
            let h = try await store.client.scanRepoConversations(repoRoot: repoRoot)
            await MainActor.run { store.setRepoHealth(h) }
            store.flash("扫描完成。")
        } catch {
            store.bannerError = "扫描失败：\(error.localizedDescription)"
        }
    }

    private func runImport() async {
        importing = true
        defer { importing = false }
        do {
            _ = try await store.client.importAllLocalHistory()
            store.flash("导入完成。重新加载各 agent 对话…")
            for kind in store.sources.compactMap(\.agentKind) {
                await store.loadConversations(for: kind)
            }
        } catch {
            store.bannerError = "导入失败：\(error.localizedDescription)"
        }
    }

}
