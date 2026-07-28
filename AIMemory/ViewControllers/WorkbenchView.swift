import SwiftUI

/// Workbench dashboard: surfaces the current agent, conversation counts,
/// a few recent tasks, and entry points. Mirrors the React app's "工作台".
struct WorkbenchView: View {
    @ObservedObject var store: AppStore

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 20) {
                heroCard
                metricsGrid
                recentTasksCard
                derivedWorkspace
            }
            .padding(Theme.outerPadding)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .background(Theme.appBackground)
    }

    private var heroCard: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(alignment: .top, spacing: 20) {
                VStack(alignment: .leading, spacing: 5) {
                    Text("继续工作")
                        .font(Theme.appFont(size: 24, weight: .bold))
                    Text("把最近进度、恢复命令、项目记忆和下一步集中在一个工作台里。")
                        .font(Theme.appFont(size: 13))
                        .foregroundStyle(Theme.secondaryText)
                }
                Spacer()
                HStack(spacing: 7) {
                    workbenchIconButton(
                        "立即同步",
                        icon: "arrow.triangle.2.circlepath",
                        isBusy: store.syncInProgress,
                        disabled: store.syncInProgress
                    ) {
                        Task { await store.syncNow() }
                    }
                    workbenchIconButton(
                        "刷新当前来源",
                        icon: "arrow.clockwise"
                    ) {
                        Task { await store.reloadCurrentAgent() }
                    }
                    workbenchIconButton(
                        "历史",
                        icon: "clock.arrow.circlepath"
                    ) {
                        store.openWorkspace(.history)
                    }
                }
                Label("\(store.currentConversations.count) 条对话", systemImage: "bubble.left.and.bubble.right")
                    .font(Theme.appFont(size: 12, weight: .medium))
                    .foregroundStyle(Theme.secondaryText)
                    .padding(.horizontal, 10)
                    .padding(.vertical, 6)
                    .background(Theme.soft)
                    .clipShape(Capsule())
            }
            HStack(spacing: 10) {
                Label(store.selectedAgent.label, systemImage: "cpu")
                    .font(Theme.appFont(size: 12, weight: .medium))
                    .padding(.horizontal, 10).padding(.vertical, 6)
                    .background(Theme.accent.opacity(0.12))
                    .foregroundStyle(Theme.accentStrong)
                    .fixedSize()
                    .clipShape(Capsule())
                if let syncMessage = store.syncStatusMessage {
                    HStack(spacing: 6) {
                        if store.syncInProgress {
                            ProgressView()
                                .controlSize(.mini)
                        } else {
                            Image(systemName: syncStatusIcon)
                                .foregroundStyle(syncStatusColor)
                        }
                        Text(syncMessage)
                            .font(Theme.appFont(size: 11, weight: .medium))
                            .foregroundStyle(Theme.secondaryText)
                            .lineLimit(2)
                    }
                    .accessibilityIdentifier("workbench-sync-status")
                }
                Spacer()
            }
            if !store.pendingCandidates.isEmpty {
                Button(action: {store.openWorkspace(.review)}) {
                    HStack(spacing: 6) {
                        Image(systemName: "checklist")
                        Text("\(store.pendingCandidates.count) 条候选规则待审")
                            .font(Theme.appFont(size: 12, weight: .medium))
                        Spacer()
                        Image(systemName: "chevron.right").font(Theme.appFont(size: 10))
                    }
                    .padding(.horizontal, 12).padding(.vertical, 8)
                    .background(Theme.accent.opacity(0.10))
                    .foregroundStyle(Theme.accentStrong)
                    .clipShape(RoundedRectangle(cornerRadius: 8))
                }
                .buttonStyle(.plain)
            }
        }
        .surfaceCard()
    }

    private func workbenchIconButton(
        _ label: String,
        icon: String,
        isBusy: Bool = false,
        disabled: Bool = false,
        action: @escaping () -> Void
    ) -> some View {
        Button(action: action) {
            Group {
                if isBusy {
                    ProgressView()
                        .controlSize(.mini)
                } else {
                    Image(systemName: icon)
                        .font(Theme.appFont(size: 13, weight: .medium))
                }
            }
            .frame(width: 18, height: 18)
        }
        .buttonStyle(.bordered)
        .controlSize(.regular)
        .disabled(disabled)
        .help(isBusy ? Text("正在同步…") : Text(LocalizedStringKey(label)))
        .accessibilityLabel(Text(LocalizedStringKey(label)))
    }

    private var syncStatusIcon: String {
        switch store.syncStatusKind {
        case .success: "checkmark.circle.fill"
        case .warning: "exclamationmark.triangle.fill"
        case .failure: "xmark.octagon.fill"
        case nil: "info.circle.fill"
        }
    }

    private var syncStatusColor: Color {
        switch store.syncStatusKind {
        case .success: .green
        case .warning: .orange
        case .failure: .red
        case nil: Theme.mutedText
        }
    }

    private var metricsGrid: some View {
        LazyVGrid(columns: [
            GridItem(.flexible(), spacing: 12),
            GridItem(.flexible(), spacing: 12),
            GridItem(.flexible(), spacing: 12),
            GridItem(.flexible(), spacing: 12),
        ], spacing: 12) {
            MetricTile(icon: "bubble.left.and.bubble.right",
                       label: "本来源对话",
                       value: "\(store.currentConversations.count)")
            Button {
                Task { await store.loadAllAgentConversations() }
            } label: {
                MetricTile(icon: "tray.full",
                           label: "全 agent 对话",
                           value: "\(allAgentConversationCount)",
                           actionLabel: "加载全部来源")
            }
            .buttonStyle(.plain)
            .frame(maxWidth: .infinity)
            Button {
                store.openWorkspace(.review)
            } label: {
                MetricTile(icon: "checkmark.seal",
                           label: "待审候选",
                           value: "\(store.pendingCandidates.count)",
                           actionLabel: "打开待复核")
            }
            .buttonStyle(.plain)
            .frame(maxWidth: .infinity)
            Button {
                store.openWorkspace(.history)
            } label: {
                MetricTile(icon: "checkmark.seal",
                           label: "检查点",
                           value: "\(store.checkpoints.count)",
                           actionLabel: "打开历史")
            }
            .buttonStyle(.plain)
            .frame(maxWidth: .infinity)
        }
    }

    /// Sum of conversation counts across every loaded agent. Note: only agents
    /// the user has selected get loaded into `store.conversations`, so this
    /// underestimates until the user browses each source. Clicking the tile
    /// triggers a load of all sources.
    private var allAgentConversationCount: Int {
        store.conversations.values.flatMap { $0 }.count
    }

    private var recentTasksCard: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Text("最近任务").font(Theme.appFont(size: 15, weight: .semibold))
                Spacer()
                Text("\(store.currentConversations.count) 条")
                    .font(Theme.appFont(size: 11))
                    .foregroundStyle(Theme.mutedText)
            }
            if store.currentConversations.isEmpty {
                emptyRow("暂无 \(store.selectedAgent.label) 对话")
            } else {
                ForEach(store.currentConversations.prefix(6)) { conv in
                    Button(action: {store.selectConversation(conv.id)}) {
                        HStack(spacing: 10) {
                            RoundedRectangle(cornerRadius: 5).fill(Theme.accent.opacity(0.18))
                                .frame(width: 6)
                            VStack(alignment: .leading, spacing: 2) {
                                Text(conv.displayTitle)
                                    .font(Theme.appFont(size: 13, weight: .medium))
                                    .lineLimit(1)
                                Text(conv.projectLeaf)
                                    .font(Theme.appFont(size: 11))
                                    .foregroundStyle(Theme.secondaryText)
                                    .lineLimit(1)
                            }
                            Spacer()
                            Text("\(conv.messageCount) 条消息")
                                .font(Theme.appFont(size: 11))
                                .foregroundStyle(Theme.mutedText)
                        }
                        .padding(.vertical, 6).padding(.horizontal, 8)
                        .background(Theme.soft)
                        .clipShape(RoundedRectangle(cornerRadius: 6))
                    }
                    .buttonStyle(.plain)
                }
            }
        }
        .card()
    }

    private var derivedWorkspace: some View {
        WorkbenchMasonryLayout(minimumColumnWidth: 310, spacing: 12) {
            workbenchPanel("02", "收藏夹增强", "置顶、备注、标签和继续卡片。") {
                HStack {
                    Text("\(store.favoriteConversations.count)")
                        .font(Theme.appFont(size: 24, weight: .bold))
                    Text("条收藏").foregroundStyle(Theme.secondaryText)
                    Spacer()
                    Button("打开") { store.openWorkspace(.favorites) }
                }
            }
            workbenchPanel("03", "项目时间线", "按项目路径聚合最近活动。") {
                if store.projectGroups.isEmpty {
                    emptyRow("暂无项目活动")
                } else {
                    ForEach(store.projectGroups.prefix(4)) { project in
                        compactConversationButton(
                            title: project.label,
                            subtitle: "\(project.conversations.count) 段对话 · \(project.conversations.reduce(0) { $0 + $1.fileCount }) 个文件",
                            conversation: project.conversations.first
                        )
                    }
                }
            }
            workbenchPanel("04", "跨 agent 接续推荐", "根据最近对话信号给出接续建议。") {
                VStack(alignment: .leading, spacing: 5) {
                    HStack {
                        Text(recommendedAgent.label)
                            .font(Theme.appFont(size: 18, weight: .semibold))
                        Spacer()
                        if recommendedAgent != store.selectedAgent {
                            Button("切换来源") {
                                store.selectAgent(recommendedAgent)
                            }
                            .buttonStyle(.bordered)
                        }
                    }
                    Text(LocalizedStringKey(recommendationReason))
                        .font(Theme.appFont(size: 11))
                        .foregroundStyle(Theme.secondaryText)
                }
            }
            workbenchPanel("05", "项目记忆沉淀", "汇总已批准规则、候选记忆和 Wiki 页面。") {
                HStack {
                    miniStat(store.approvedMemories.count, "规则")
                    miniStat(store.pendingCandidates.count, "待确认")
                    miniStat(store.wikiPages.count, "Wiki")
                    Spacer()
                    Button("打开记忆") { store.openMemoryDrawer(tab: .rules) }
                }
            }
            workbenchPanel("06", "更强搜索", "结果带有项目与新近程度线索。") {
                let matches = (store.searchResults ?? store.filteredConversations)
                    .prefix(4)
                if matches.isEmpty {
                    emptyRow(store.searchQuery.isEmpty ? "在左侧输入关键词开始搜索" : "没有匹配对话")
                } else {
                    ForEach(Array(matches)) { conversation in
                        compactConversationButton(
                            title: conversation.displayTitle,
                            subtitle: conversation.projectLeaf,
                            conversation: conversation
                        )
                    }
                }
            }
            workbenchPanel("07", "发布准备状态", "发布前检查版本、数据保护和更新通道。") {
                readinessRow("应用版本", Bundle.main.object(
                    forInfoDictionaryKey: "CFBundleShortVersionString"
                ) as? String ?? "0.1.0", ok: true)
                readinessRow("独立数据目录", DataPaths.supportDir.path, ok: true)
                readinessRow(
                    "更新通道",
                    updateFeedConfigured ? "已配置" : "未配置（可在设置中填写）",
                    ok: updateFeedConfigured
                )
            }
            workbenchPanel("08", "对话质量整理", "优先展示包含文件或长上下文的高信号对话。") {
                let rows = highSignalConversations.prefix(3)
                if rows.isEmpty {
                    emptyRow("暂无高信号候选")
                } else {
                    ForEach(Array(rows)) { conversation in
                        compactConversationButton(
                            title: conversation.displayTitle,
                            subtitle: "\(conversation.messageCount) 条消息 · \(conversation.fileCount) 个文件",
                            conversation: conversation
                        )
                    }
                }
            }
            workbenchPanel("09", "本地隐私与清理", "仅列出低信号旧对话供确认，不自动删除。") {
                let rows = cleanupCandidates.prefix(3)
                if rows.isEmpty {
                    emptyRow("暂时没有明显清理候选")
                } else {
                    ForEach(Array(rows)) { conversation in
                        compactConversationButton(
                            title: conversation.displayTitle,
                            subtitle: "\(conversation.messageCount) 条消息 · \(conversation.updatedAt)",
                            conversation: conversation
                        )
                    }
                }
            }
            workbenchPanel("10", "macOS 原生对等状态", "关键能力使用 SwiftUI、AppKit 与 Apple Framework。") {
                readinessRow("本地历史与搜索", "原生 SQLite / Foundation", ok: true)
                readinessRow("同步与凭据", "URLSession / Keychain", ok: true)
                readinessRow("窗口、菜单与状态栏", "SwiftUI / AppKit", ok: true)
                readinessRow("ChatMem 数据保护", "只读导入 + 备份回滚", ok: true)
            }
        }
    }

    private var allConversations: [ConversationSummary] {
        store.conversations.values.flatMap { $0 }
    }

    private var highSignalConversations: [ConversationSummary] {
        allConversations
            .filter { $0.fileCount > 0 || $0.messageCount >= 12 || store.isFavorite($0.id) }
            .sorted {
                ($0.fileCount * 5 + $0.messageCount)
                    > ($1.fileCount * 5 + $1.messageCount)
            }
    }

    private var cleanupCandidates: [ConversationSummary] {
        let threshold = Date().addingTimeInterval(-90 * 24 * 60 * 60)
        return allConversations.filter {
            $0.fileCount == 0 && $0.messageCount <= 2
                && (date(from: $0.updatedAt) ?? .distantFuture) < threshold
                && !store.isFavorite($0.id)
        }
    }

    private var recommendedAgent: AgentKind {
        guard let latest = allConversations.max(by: { $0.updatedAt < $1.updatedAt }) else {
            return store.selectedAgent
        }
        if latest.fileCount > 0 { return .codex }
        return latest.agentKind ?? store.selectedAgent
    }

    private var recommendationReason: String {
        guard let latest = allConversations.max(by: { $0.updatedAt < $1.updatedAt }) else {
            return "暂无最近对话，保留当前来源。"
        }
        return latest.fileCount > 0
            ? "最近任务包含文件变更，Codex 的代码接续路径最直接。"
            : "留在 \(latest.agentKind?.label ?? latest.sourceAgent) 继续，迁移成本最低。"
    }

    private var updateFeedConfigured: Bool {
        let raw = (store.appSettings?["updateFeedURL"] as? String)
            ?? (store.appSettings?["update_feed_url"] as? String)
            ?? ""
        return !raw.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }

    private func workbenchPanel<Content: View>(
        _ index: String,
        _ title: String,
        _ subtitle: String,
        @ViewBuilder content: () -> Content
    ) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(alignment: .top, spacing: 9) {
                Text(index)
                    .font(Theme.appFont(size: 10, weight: .bold, design: .monospaced))
                    .foregroundStyle(Theme.accentStrong)
                VStack(alignment: .leading, spacing: 2) {
                    Text(LocalizedStringKey(title))
                        .font(Theme.appFont(size: 14, weight: .semibold))
                    Text(LocalizedStringKey(subtitle))
                        .font(Theme.appFont(size: 10))
                        .foregroundStyle(Theme.secondaryText)
                }
            }
            content()
        }
        .card()
    }

    private func miniStat(_ value: Int, _ label: String) -> some View {
        VStack(spacing: 2) {
            Text("\(value)").font(Theme.appFont(size: 17, weight: .bold))
            Text(LocalizedStringKey(label))
                .font(Theme.appFont(size: 9))
                .foregroundStyle(Theme.mutedText)
        }
        .frame(minWidth: 54)
    }

    private func compactConversationButton(
        title: String,
        subtitle: String,
        conversation: ConversationSummary?
    ) -> some View {
        Button {
            guard let conversation else { return }
            if let agent = conversation.agentKind, agent != store.selectedAgent {
                store.selectAgent(agent)
            }
            store.selectConversation(conversation.id)
        } label: {
            HStack {
                VStack(alignment: .leading, spacing: 2) {
                    Text(title).font(Theme.appFont(size: 11, weight: .medium)).lineLimit(1)
                    Text(subtitle)
                        .font(Theme.appFont(size: 9))
                        .foregroundStyle(Theme.secondaryText)
                        .lineLimit(1)
                }
                Spacer()
                Image(systemName: "chevron.right")
                    .font(Theme.appFont(size: 9))
                    .foregroundStyle(Theme.mutedText)
            }
            .padding(8)
            .background(Theme.soft)
            .clipShape(RoundedRectangle(cornerRadius: 6))
        }
        .buttonStyle(.plain)
        .disabled(conversation == nil)
    }

    private func readinessRow(_ label: String, _ value: String, ok: Bool) -> some View {
        HStack {
            Image(systemName: ok ? "checkmark.circle.fill" : "exclamationmark.circle")
                .foregroundStyle(ok ? Theme.accent : Color.orange)
            Text(LocalizedStringKey(label)).font(Theme.appFont(size: 10, weight: .medium))
            Spacer()
            Text(LocalizedStringKey(value))
                .font(Theme.appFont(size: 9))
                .foregroundStyle(Theme.secondaryText)
                .lineLimit(1)
                .truncationMode(.middle)
        }
    }

    private func date(from iso: String) -> Date? {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return formatter.date(from: iso) ?? ISO8601DateFormatter().date(from: iso)
    }

    private func emptyRow(_ text: String) -> some View {
        Text(LocalizedStringKey(text))
            .font(Theme.appFont(size: 12))
            .foregroundStyle(Theme.mutedText)
            .frame(maxWidth: .infinity, alignment: .center)
            .padding(.vertical, 16)
    }
}

/// Packs variable-height workbench cards into the shortest available column.
/// Unlike a grid, one tall card does not force the card beside it to leave an
/// empty row-sized gap. The layout collapses to one column in narrow windows.
private struct WorkbenchMasonryLayout: Layout {
    let minimumColumnWidth: CGFloat
    let spacing: CGFloat

    func sizeThatFits(
        proposal: ProposedViewSize,
        subviews: Subviews,
        cache: inout ()
    ) -> CGSize {
        let width = proposedWidth(proposal, subviews: subviews)
        let result = measurements(for: subviews, width: width)
        return CGSize(width: width, height: result.height)
    }

    func placeSubviews(
        in bounds: CGRect,
        proposal: ProposedViewSize,
        subviews: Subviews,
        cache: inout ()
    ) {
        let result = measurements(for: subviews, width: bounds.width)
        for (index, placement) in result.placements.enumerated() {
            subviews[index].place(
                at: CGPoint(
                    x: bounds.minX + placement.origin.x,
                    y: bounds.minY + placement.origin.y
                ),
                anchor: .topLeading,
                proposal: ProposedViewSize(
                    width: placement.size.width,
                    height: placement.size.height
                )
            )
        }
    }

    private func proposedWidth(_ proposal: ProposedViewSize, subviews: Subviews) -> CGFloat {
        if let width = proposal.width, width.isFinite {
            return width
        }
        return subviews
            .map { $0.sizeThatFits(.unspecified).width }
            .max() ?? minimumColumnWidth
    }

    private func measurements(
        for subviews: Subviews,
        width: CGFloat
    ) -> (placements: [CGRect], height: CGFloat) {
        let safeWidth = max(width, 1)
        let columnCount = safeWidth >= minimumColumnWidth * 2 + spacing ? 2 : 1
        let columnWidth = (safeWidth - spacing * CGFloat(columnCount - 1))
            / CGFloat(columnCount)
        var columnHeights = Array(repeating: CGFloat.zero, count: columnCount)
        var placements: [CGRect] = []
        placements.reserveCapacity(subviews.count)

        for subview in subviews {
            let column = columnHeights.enumerated()
                .min(by: { $0.element < $1.element })?.offset ?? 0
            let size = subview.sizeThatFits(
                ProposedViewSize(width: columnWidth, height: nil)
            )
            let origin = CGPoint(
                x: CGFloat(column) * (columnWidth + spacing),
                y: columnHeights[column]
            )
            placements.append(CGRect(origin: origin, size: CGSize(
                width: columnWidth,
                height: size.height
            )))
            columnHeights[column] += size.height + spacing
        }

        let height = max(0, (columnHeights.max() ?? 0) - spacing)
        return (placements, height)
    }
}

struct MetricTile: View {
    let icon: String
    let label: String
    let value: String
    var actionLabel: String? = nil

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Image(systemName: icon)
                .font(Theme.appFont(size: 16))
                .foregroundStyle(Theme.accent)
            Text(value).font(Theme.appFont(size: 22, weight: .bold))
                .fixedSize(horizontal: false, vertical: true)
            Text(LocalizedStringKey(label))
                .font(Theme.appFont(size: 11))
                .foregroundStyle(Theme.secondaryText)
                .fixedSize(horizontal: false, vertical: true)
                .lineLimit(2)
                .multilineTextAlignment(.leading)
            Group {
                if let actionLabel {
                    Label(actionLabel, systemImage: "arrow.right.circle")
                        .font(Theme.appFont(size: 9, weight: .medium))
                        .foregroundStyle(Theme.accentStrong)
                } else {
                    Color.clear
                        .frame(height: 12)
                        .accessibilityHidden(true)
                }
            }
        }
        .frame(
            minWidth: 0,
            maxWidth: .infinity,
            minHeight: 132,
            maxHeight: 132,
            alignment: .leading
        )
        .padding(16)
        .background(Theme.surface)
        .overlay(RoundedRectangle(cornerRadius: 8).stroke(Theme.border, lineWidth: 1))
        .clipShape(RoundedRectangle(cornerRadius: 8))
    }
}
