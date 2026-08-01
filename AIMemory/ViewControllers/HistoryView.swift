// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
import AppKit
import SwiftUI

/// Repository history library plus run, artifact, and episode projections.
struct HistoryView: View {
    @ObservedObject var store: AppStore
    @State private var section: HistorySection = .library
    @State private var libraryFilter: LibraryFilter = .all
    @State private var selectedHandoff: HandoffPacket?

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                HStack {
                    VStack(alignment: .leading, spacing: 6) {
                        Text("历史").font(Theme.appFont(size: 22, weight: .bold))
                        Text("对话、记忆、检查点、交接、运行与产出的统一资料库。")
                            .font(Theme.appFont(size: 12))
                            .foregroundStyle(Theme.secondaryText)
                    }
                    Spacer()
                    Button {
                        Task {
                            await store.loadRepoMemory(repoRoot: store.activeRepoRoot)
                        }
                    } label: {
                        Label("刷新", systemImage: "arrow.clockwise")
                    }
                    .buttonStyle(.bordered)
                }
                .surfaceCard()

                Picker("历史类型", selection: $section) {
                    ForEach(HistorySection.allCases) { item in
                        Text(LocalizedStringKey(item.label)).tag(item)
                    }
                }
                .pickerStyle(.segmented)

                sectionContent
            }
            .padding(Theme.outerPadding)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .background(Theme.appBackground)
        .task {
            await store.loadRepoMemory(repoRoot: store.activeRepoRoot)
        }
        .sheet(item: $selectedHandoff) { handoff in
            HandoffDetailSheet(handoff: handoff, store: store)
        }
    }

    private var totalConversationCount: Int {
        store.conversations.values.flatMap { $0 }.count
    }

    private func conversationID(for runID: String) -> String {
        runID.hasPrefix("run:") ? String(runID.dropFirst(4)) : runID
    }

    @ViewBuilder
    private var sectionContent: some View {
        switch section {
        case .library:
            library
        case .runs:
            timelineList(
                title: "运行",
                empty: "当前仓库没有需要继续处理的运行。"
            ) {
                ForEach(store.activeRuns) { run in
                    Button {
                        Task {
                            await store.openHistoricalConversation(
                                id: conversationID(for: run.runID),
                                sourceAgent: run.sourceAgent
                            )
                        }
                    } label: {
                        TimelineCard(
                            title: run.taskHint ?? run.summary,
                            kind: run.sourceAgent,
                            status: run.status,
                            summary: run.summary,
                            footer: "\(run.startedAt) · \(run.artifactCount) 个产物",
                            actionLabel: "打开来源对话"
                        )
                    }
                    .buttonStyle(.plain)
                }
            }
        case .artifacts:
            timelineList(
                title: "产物",
                empty: "当前仓库尚无运行产物。"
            ) {
                ForEach(store.runArtifacts) { artifact in
                    Button {
                        Task {
                            await store.openHistoricalConversation(
                                id: conversationID(for: artifact.runID)
                            )
                        }
                    } label: {
                        TimelineCard(
                            title: artifact.title,
                            kind: artifact.artifactType,
                            status: artifact.trustState,
                            summary: artifact.summary,
                            footer: "\(artifact.runID) · \(artifact.createdAt)",
                            actionLabel: "查看产生该产物的对话"
                        )
                    }
                    .buttonStyle(.plain)
                }
            }
        case .episodes:
            timelineList(
                title: "经历",
                empty: "当前仓库尚未沉淀经历卡。"
            ) {
                ForEach(store.episodes) { episode in
                    Button {
                        Task {
                            await store.openHistoricalConversation(
                                id: episode.sourceConversationID
                            )
                        }
                    } label: {
                        TimelineCard(
                            title: episode.title,
                            kind: "episode",
                            status: episode.outcome,
                            summary: episode.summary,
                            footer: episode.createdAt,
                            actionLabel: "打开来源对话"
                        )
                    }
                    .buttonStyle(.plain)
                }
            }
        case .wiki:
            timelineList(title: "Wiki 投影", empty: "暂无 Wiki 投影。") {
                ForEach(store.wikiPages) { page in
                    WikiPageCard(page: page)
                }
            }
        case .graph:
            entityGraph
        case .local:
            LocalHistoryIndexView(store: store)
        }
    }

    private var library: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                VStack(alignment: .leading, spacing: 3) {
                    Text("项目上下文")
                        .font(Theme.appFont(size: 16, weight: .semibold))
                    Text(store.activeRepoRoot)
                        .font(Theme.appFont(size: 10, design: .monospaced))
                        .foregroundStyle(Theme.mutedText)
                        .lineLimit(1)
                        .truncationMode(.middle)
                }
                Spacer()
                Text("\(libraryCount) 项")
                    .font(Theme.appFont(size: 11))
                    .foregroundStyle(Theme.mutedText)
            }
            Picker("资料类型", selection: $libraryFilter) {
                ForEach(LibraryFilter.allCases) { filter in
                    (
                        Text(LocalizedStringKey(filter.label))
                        + Text(" (\(count(for: filter)))")
                    ).tag(filter)
                }
            }
            .pickerStyle(.segmented)

            if count(for: libraryFilter) == 0 {
                EmptyNote("这个筛选下还没有条目。")
            } else {
                if libraryFilter == .all || libraryFilter == .conversation {
                    ForEach(repoConversations) { conversation in
                        Button {
                            if let agent = conversation.agentKind {
                                store.selectAgent(agent)
                            }
                            store.selectConversation(conversation.id)
                        } label: {
                            LibraryRow(
                                kind: "对话",
                                title: conversation.displayTitle,
                                subtitle: conversation.sourceAgent,
                                status: "\(conversation.messageCount) 条消息",
                                timestamp: conversation.updatedAt
                            )
                        }
                        .buttonStyle(.plain)
                    }
                }
                if libraryFilter == .all || libraryFilter == .memory {
                    ForEach(store.approvedMemories) { memory in
                        Button {
                            store.openMemoryDrawer(tab: .rules)
                        } label: {
                            LibraryRow(
                                kind: "记忆",
                                title: memory.title,
                                subtitle: memory.value,
                                status: memory.freshnessLabel,
                                timestamp: memory.lastVerifiedAt ?? ""
                            )
                        }
                        .buttonStyle(.plain)
                    }
                }
                if libraryFilter == .all || libraryFilter == .checkpoint {
                    ForEach(store.checkpoints) { checkpoint in
                        Button {
                            Task {
                                await store.openHistoricalConversation(
                                    id: checkpoint.conversationID,
                                    sourceAgent: checkpoint.sourceAgent
                                )
                            }
                        } label: {
                            LibraryRow(
                                kind: "检查点",
                                title: checkpoint.summary,
                                subtitle: checkpoint.sourceAgent,
                                status: checkpoint.status,
                                timestamp: checkpoint.createdAt,
                                actionLabel: "打开来源对话"
                            )
                        }
                        .buttonStyle(.plain)
                    }
                }
                if libraryFilter == .all || libraryFilter == .handoff {
                    ForEach(store.handoffs) { handoff in
                        Button {
                            openHandoff(handoff)
                        } label: {
                            LibraryRow(
                                kind: "交接",
                                title: handoff.currentGoal,
                                subtitle: "\(handoff.fromAgent) → \(handoff.toAgent)",
                                status: handoff.status,
                                timestamp: handoff.createdAt ?? "",
                                actionLabel: "查看交接详情"
                            )
                        }
                        .buttonStyle(.plain)
                    }
                }
            }
        }
        .card()
    }

    private var repoConversations: [ConversationSummary] {
        store.conversations.values.flatMap { $0 }.filter {
            $0.projectDir == store.activeRepoRoot
        }
    }

    private var libraryCount: Int {
        repoConversations.count + store.approvedMemories.count
            + store.checkpoints.count + store.handoffs.count
    }

    private func count(for filter: LibraryFilter) -> Int {
        switch filter {
        case .all: libraryCount
        case .conversation: repoConversations.count
        case .memory: store.approvedMemories.count
        case .checkpoint: store.checkpoints.count
        case .handoff: store.handoffs.count
        }
    }

    private func openHandoff(_ handoff: HandoffPacket) {
        selectedHandoff = handoff
    }

    private func timelineList<Content: View>(
        title: String,
        empty: String,
        @ViewBuilder content: () -> Content
    ) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(LocalizedStringKey(title)).font(Theme.appFont(size: 15, weight: .semibold))
            let isEmpty: Bool = {
                switch section {
                case .runs: store.activeRuns.isEmpty
                case .artifacts: store.runArtifacts.isEmpty
                case .episodes: store.episodes.isEmpty
                case .wiki: store.wikiPages.isEmpty
                default: false
                }
            }()
            if isEmpty { EmptyNote(empty) } else { content() }
        }
        .card()
    }

    private var entityGraph: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("实体图谱").font(Theme.appFont(size: 15, weight: .semibold))
            if store.entityGraph.entities.isEmpty {
                EmptyNote("当前仓库尚无实体关系。批准记忆并重建索引后会生成。")
            } else {
                ForEach(store.entityGraph.entities) { entity in
                    VStack(alignment: .leading, spacing: 5) {
                        HStack {
                            Label(entity.name, systemImage: "circle.hexagongrid")
                                .font(Theme.appFont(size: 12, weight: .medium))
                            Spacer()
                            Text("\(entity.mentionCount) 次引用")
                                .font(Theme.appFont(size: 10))
                                .foregroundStyle(Theme.mutedText)
                        }
                        ForEach(
                            store.entityGraph.links.filter {
                                $0.entityID == entity.entityID
                            }.prefix(4)
                        ) { link in
                            Button {
                                openEntityLink(link)
                            } label: {
                                HStack(spacing: 6) {
                                    Text("\(link.relationship) · \(link.sourceTitle)")
                                        .lineLimit(1)
                                    Spacer()
                                    Image(systemName: "arrow.right.circle")
                                }
                                .font(Theme.appFont(size: 10))
                                .foregroundStyle(Theme.secondaryText)
                                .contentShape(Rectangle())
                            }
                            .buttonStyle(.plain)
                            .help("打开关联内容")
                        }
                    }
                    .padding(10)
                    .background(Theme.soft)
                    .clipShape(RoundedRectangle(cornerRadius: 7))
                }
            }
        }
        .card()
    }

    private func openEntityLink(_ link: MemoryEntityLink) {
        switch link.ownerType {
        case "chunk":
            guard let conversationID = link.sourceConversationID else {
                store.showUserError("找不到这个片段对应的来源对话。")
                return
            }
            Task {
                await store.openHistoricalConversation(id: conversationID)
            }
        case "conversation":
            Task { await store.openHistoricalConversation(id: link.ownerID) }
        case "episode":
            guard let episode = store.episodes.first(where: {
                $0.episodeID == link.ownerID
            }) else {
                store.showUserError("找不到这条经历对应的来源对话。")
                return
            }
            Task {
                await store.openHistoricalConversation(
                    id: episode.sourceConversationID
                )
            }
        case "memory":
            store.openMemoryDrawer(tab: .rules)
        case "wiki_page":
            section = .wiki
        default:
            store.showUserError("这个关联类型暂时没有可打开的内容。")
        }
    }
}

struct HandoffDetailSheet: View {
    let handoff: HandoffPacket
    @ObservedObject var store: AppStore
    @Environment(\.dismiss) private var dismiss

    private var sourceCheckpoint: Checkpoint? {
        guard let checkpointID = handoff.checkpointID else { return nil }
        return store.checkpoints.first { $0.checkpointID == checkpointID }
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                HStack(alignment: .top) {
                    VStack(alignment: .leading, spacing: 4) {
                        Text("交接详情")
                            .font(Theme.appFont(size: 20, weight: .bold))
                        Text("\(handoff.fromAgent) → \(handoff.toAgent) · \(handoff.status)")
                            .font(Theme.appFont(size: 11))
                            .foregroundStyle(Theme.mutedText)
                    }
                    Spacer()
                    Button("关闭") { dismiss() }
                        .keyboardShortcut(.cancelAction)
                }

                detailSection("当前目标", values: [handoff.currentGoal])
                detailSection("已完成", values: handoff.doneItems)
                detailSection("下一步", values: handoff.nextItems)
                detailSection("关键文件", values: handoff.keyFiles, monospaced: true)
                detailSection("可用命令", values: handoff.usefulCommands, monospaced: true)

                HStack {
                    if let checkpoint = sourceCheckpoint {
                        Button {
                            Task {
                                await store.openHistoricalConversation(
                                    id: checkpoint.conversationID,
                                    sourceAgent: checkpoint.sourceAgent
                                )
                                dismiss()
                            }
                        } label: {
                            Label("打开来源对话", systemImage: "arrow.right.circle")
                        }
                        .buttonStyle(.bordered)
                    }
                    Spacer()
                    Button {
                        NSPasteboard.general.clearContents()
                        NSPasteboard.general.setString(copyText, forType: .string)
                        store.flash("交接内容已复制。")
                    } label: {
                        Label("复制交接内容", systemImage: "doc.on.doc")
                    }
                    .buttonStyle(.bordered)
                    if handoff.status != "consumed" {
                        Button("标记为已消费") {
                            Task {
                                await store.consumeHandoff(handoff)
                                dismiss()
                            }
                        }
                        .buttonStyle(.borderedProminent)
                    }
                }
            }
            .padding(22)
        }
        .frame(minWidth: 560, idealWidth: 640, minHeight: 440, idealHeight: 620)
    }

    @ViewBuilder
    private func detailSection(
        _ title: String,
        values: [String],
        monospaced: Bool = false
    ) -> some View {
        if !values.isEmpty {
            VStack(alignment: .leading, spacing: 7) {
                Text(LocalizedStringKey(title))
                    .font(Theme.appFont(size: 13, weight: .semibold))
                ForEach(Array(values.enumerated()), id: \.offset) { _, value in
                    HStack(alignment: .top, spacing: 7) {
                        Text("•").foregroundStyle(Theme.accent)
                        Text(value)
                            .font(
                                monospaced
                                    ? .system(size: 11, design: .monospaced)
                                    : .system(size: 12)
                            )
                            .textSelection(.enabled)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                }
            }
            .card()
        }
    }

    private var copyText: String {
        var lines = [
            "# \(handoff.currentGoal)",
            "",
            "\(handoff.fromAgent) -> \(handoff.toAgent)",
        ]
        append("已完成", handoff.doneItems, to: &lines)
        append("下一步", handoff.nextItems, to: &lines)
        append("关键文件", handoff.keyFiles, to: &lines)
        append("可用命令", handoff.usefulCommands, to: &lines)
        return lines.joined(separator: "\n")
    }

    private func append(_ title: String, _ values: [String], to lines: inout [String]) {
        guard !values.isEmpty else { return }
        lines.append(contentsOf: ["", "## \(title)"])
        lines.append(contentsOf: values.map { "- \($0)" })
    }
}

private enum HistorySection: String, CaseIterable, Identifiable {
    case library, local, runs, artifacts, episodes, wiki, graph
    var id: String { rawValue }
    var label: String {
        switch self {
        case .library: "资料库"
        case .local: "本地索引"
        case .runs: "运行"
        case .artifacts: "产物"
        case .episodes: "经历"
        case .wiki: "Wiki"
        case .graph: "图谱"
        }
    }
}

private enum LibraryFilter: String, CaseIterable, Identifiable {
    case all, conversation, memory, checkpoint, handoff
    var id: String { rawValue }
    var label: String {
        switch self {
        case .all: "全部"
        case .conversation: "对话"
        case .memory: "记忆"
        case .checkpoint: "检查点"
        case .handoff: "交接"
        }
    }
}

private struct LibraryRow: View {
    let kind: String
    let title: String
    let subtitle: String
    let status: String
    let timestamp: String
    var actionLabel: String? = nil

    var body: some View {
        HStack(alignment: .top, spacing: 10) {
            Text(LocalizedStringKey(kind))
                .font(Theme.appFont(size: 10, weight: .semibold))
                .foregroundStyle(Theme.accentStrong)
                .padding(.horizontal, 7).padding(.vertical, 3)
                .background(Theme.accent.opacity(0.12))
                .clipShape(Capsule())
            VStack(alignment: .leading, spacing: 3) {
                Text(title).font(Theme.appFont(size: 12, weight: .medium)).lineLimit(2)
                Text(subtitle)
                    .font(Theme.appFont(size: 10))
                    .foregroundStyle(Theme.secondaryText)
                    .lineLimit(2)
                Text(timestamp)
                    .font(Theme.appFont(size: 9))
                    .foregroundStyle(Theme.mutedText)
            }
            Spacer()
            Text(status)
                .font(Theme.appFont(size: 9))
                .foregroundStyle(Theme.mutedText)
            if let actionLabel {
                Label(actionLabel, systemImage: "arrow.right.circle")
                    .font(Theme.appFont(size: 9, weight: .medium))
                    .foregroundStyle(Theme.accentStrong)
            }
        }
        .padding(10)
        .background(Theme.soft)
        .clipShape(RoundedRectangle(cornerRadius: 7))
    }
}

private struct TimelineCard: View {
    let title: String
    let kind: String
    let status: String
    let summary: String
    let footer: String
    let actionLabel: String

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack {
                VStack(alignment: .leading, spacing: 2) {
                    Text(title).font(Theme.appFont(size: 12, weight: .semibold))
                    Text(kind).font(Theme.appFont(size: 9)).foregroundStyle(Theme.mutedText)
                }
                Spacer()
                Text(status)
                    .font(Theme.appFont(size: 9, weight: .medium))
                    .padding(.horizontal, 7).padding(.vertical, 3)
                    .background(Theme.accent.opacity(0.12))
                    .clipShape(Capsule())
            }
            Text(summary)
                .font(Theme.appFont(size: 11))
                .foregroundStyle(Theme.secondaryText)
                .fixedSize(horizontal: false, vertical: true)
            HStack(spacing: 8) {
                Text(footer)
                    .font(Theme.appFont(size: 9))
                    .foregroundStyle(Theme.mutedText)
                Spacer()
                Label(actionLabel, systemImage: "arrow.right.circle")
                    .font(Theme.appFont(size: 10, weight: .medium))
                    .foregroundStyle(Theme.accentStrong)
            }
        }
        .padding(10)
        .background(Theme.soft)
        .clipShape(RoundedRectangle(cornerRadius: 7))
    }
}

// MARK: - Favorites

struct FavoritesView: View {
    @ObservedObject var store: AppStore

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 14) {
                VStack(alignment: .leading, spacing: 6) {
                    Text("收藏").font(Theme.appFont(size: 22, weight: .bold))
                    Text("收藏保存轻量快照，不复制或移动原始对话。")
                        .font(Theme.appFont(size: 12))
                        .foregroundStyle(Theme.secondaryText)
                }
                .surfaceCard()
                if store.favoriteConversations.isEmpty {
                    EmptyNote("暂无收藏。在对话详情或左侧列表点击星标后，对话会显示在这里。")
                } else {
                    LazyVStack(alignment: .leading, spacing: 8) {
                        ForEach(store.favoriteConversations) { conversation in
                            FavoriteConversationCard(
                                store: store,
                                conversation: conversation
                            )
                        }
                    }
                }
            }
            .padding(Theme.outerPadding)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .background(Theme.appBackground)
    }
}

private struct FavoriteConversationCard: View {
    @ObservedObject var store: AppStore
    let conversation: ConversationSummary
    @State private var note = ""
    @State private var tags = ""

    private var agent: AgentKind {
        conversation.agentKind ?? .codex
    }

    private var snapshot: FavoriteConversationSnapshot? {
        store.favoriteSnapshot(
            conversationID: conversation.id,
            agent: agent
        )
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 10) {
                Image(systemName: "star.fill")
                    .foregroundStyle(Theme.accent)
                VStack(alignment: .leading, spacing: 3) {
                    HStack(spacing: 6) {
                        Text(conversation.displayTitle)
                            .font(Theme.appFont(size: 13, weight: .medium))
                            .lineLimit(2)
                        if snapshot?.pinned == true {
                            Text("已置顶")
                                .font(Theme.appFont(size: 9, weight: .semibold))
                                .foregroundStyle(Theme.accentStrong)
                                .padding(.horizontal, 6)
                                .padding(.vertical, 2)
                                .background(Theme.accent.opacity(0.14))
                                .clipShape(Capsule())
                        }
                    }
                    Text("\(conversation.sourceAgent) · \(conversation.projectDir)")
                        .font(Theme.appFont(size: 10))
                        .foregroundStyle(Theme.mutedText)
                        .lineLimit(1)
                        .truncationMode(.middle)
                }
                Spacer()
            }

            HStack(spacing: 8) {
                VStack(alignment: .leading, spacing: 3) {
                    Text("备注")
                        .font(Theme.appFont(size: 10))
                        .foregroundStyle(Theme.mutedText)
                    TextField("这段对话为什么重要？", text: $note)
                        .textFieldStyle(.roundedBorder)
                }
                VStack(alignment: .leading, spacing: 3) {
                    Text("标签")
                        .font(Theme.appFont(size: 10))
                        .foregroundStyle(Theme.mutedText)
                    TextField("发布, 修复", text: $tags)
                        .textFieldStyle(.roundedBorder)
                }
            }

            HStack(spacing: 8) {
                Button(snapshot?.pinned == true ? "取消置顶" : "置顶") {
                    store.updateFavorite(
                        conversationID: conversation.id,
                        agent: agent,
                        pinned: !(snapshot?.pinned ?? false)
                    )
                }
                Button("保存备注与标签") {
                    store.updateFavorite(
                        conversationID: conversation.id,
                        agent: agent,
                        note: note,
                        tags: tags.split(separator: ",").map(String.init)
                    )
                }
                Button("打开对话") {
                    store.selectAgent(agent)
                    store.selectConversation(conversation.id)
                }
                Button("复制继续卡片") {
                    NSPasteboard.general.clearContents()
                    NSPasteboard.general.setString(
                        store.favoriteContinuationCard(for: conversation),
                        forType: .string
                    )
                    store.flash("收藏继续卡片已复制。")
                }
                Spacer()
                Button("取消收藏", role: .destructive) {
                    store.toggleFavorite(conversation.id, agent: agent)
                }
            }
            .buttonStyle(.bordered)
            .controlSize(.small)
        }
        .padding(12)
        .background(Theme.surface)
        .overlay(
            RoundedRectangle(cornerRadius: 8)
                .stroke(Theme.border, lineWidth: 1)
        )
        .clipShape(RoundedRectangle(cornerRadius: 8))
        .onAppear {
            note = snapshot?.note ?? ""
            tags = snapshot?.tags.joined(separator: ", ") ?? ""
        }
    }
}

// MARK: - Trash

struct TrashView: View {
    @ObservedObject var store: AppStore
    @State private var showEmptyConfirm = false

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 14) {
                VStack(alignment: .leading, spacing: 6) {
                    HStack {
                        VStack(alignment: .leading, spacing: 2) {
                            Text("回收站").font(Theme.appFont(size: 22, weight: .bold))
                            Text("已删除对话的可恢复记录（保留 \(trashRetentionDays) 天）。")
                                .font(Theme.appFont(size: 12))
                                .foregroundStyle(Theme.secondaryText)
                        }
                        Spacer()
                        Stepper(
                            "保留 \(store.trashRetentionDays) 天",
                            value: Binding(
                                get: { store.trashRetentionDays },
                                set: { value in
                                    Task { await store.setTrashRetentionDays(value) }
                                }
                            ),
                            in: 1...365
                        )
                        .fixedSize()
                        if !store.trashed.isEmpty {
                            Button(role: .destructive) {
                                showEmptyConfirm = true
                            } label: {
                                Label("清空", systemImage: "trash")
                            }
                            .buttonStyle(.bordered)
                        }
                        Button(action: {Task { await store.loadTrashed() }}) {
                            Label("刷新", systemImage: "arrow.clockwise")
                        }
                        .buttonStyle(.bordered)
                    }
                }
                .surfaceCard()

                if store.trashed.isEmpty {
                    EmptyNote("回收站为空。在对话详情点击「删除」后，对话会在此保留 \(trashRetentionDays) 天供恢复。")
                } else {
                    LazyVStack(alignment: .leading, spacing: 8) {
                        ForEach(store.trashed) { rec in
                            TrashRow(rec: rec) {
                                Task {
                                    await store.restoreTrashed(
                                        trashID: rec.trashID,
                                        agent: rec.sourceAgent
                                    )
                                }
                            } onDelete: {
                                Task {
                                    await store.deleteTrashRecord(
                                        trashID: rec.trashID,
                                        agent: rec.sourceAgent
                                    )
                                }
                            }
                        }
                    }
                }
            }
            .padding(Theme.outerPadding)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .background(Theme.appBackground)
        .task { await store.loadTrashed() }
        .confirmationDialog(
            "清空回收站？",
            isPresented: $showEmptyConfirm,
            titleVisibility: .visible
        ) {
            Button("永久清空（不可恢复）", role: .destructive) {
                Task { await store.emptyTrash() }
            }
            Button("取消", role: .cancel) {}
        } message: {
            Text("将永久删除全部 \(store.trashed.count) 条回收站记录。原对话数据已不可恢复。")
        }
    }

    private var trashRetentionDays: Int { store.trashRetentionDays }
}

struct TrashRow: View {
    let rec: TrashRecord
    var onRestore: () -> Void
    var onDelete: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack {
                Image(systemName: "trash").font(Theme.appFont(size: 11)).foregroundStyle(Theme.mutedText)
                Text(rec.summary ?? rec.originalID)
                    .font(Theme.appFont(size: 12, weight: .medium))
                    .lineLimit(1)
                Spacer()
                Text(rec.sourceAgent).font(Theme.appFont(size: 10)).foregroundStyle(Theme.mutedText)
            }
            HStack(spacing: 8) {
                Text(shortDate(rec.trashedAt))
                if !rec.expiresAt.isEmpty {
                    Text("· 过期 " + shortDate(rec.expiresAt))
                }
                Spacer()
                Button("恢复", action: onRestore)
                    .buttonStyle(.bordered).controlSize(.mini)
                Button("永久删除", action: onDelete)
                    .buttonStyle(.bordered).controlSize(.mini)
            }
            .font(Theme.appFont(size: 10))
            .foregroundStyle(Theme.mutedText)
        }
        .padding(10)
        .background(Theme.soft)
        .clipShape(RoundedRectangle(cornerRadius: 6))
    }

    private func shortDate(_ iso: String) -> String {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        let date = f.date(from: iso) ?? ISO8601DateFormatter().date(from: iso) ?? Date()
        let df = DateFormatter(); df.dateStyle = .short; df.timeStyle = .short
        return df.string(from: date)
    }
}

// MARK: - Help

struct HelpView: View {
    @ObservedObject var store: AppStore
    @State private var query = ""
    @State private var advancedOpen = false

    private var cards: [HelpCard] {
        [
            HelpCard(id: "continue", title: "继续之前的工作",
                     description: "回到最近一次可恢复的进度。",
                     answer: "先从“继续工作”开始。选中对话后，恢复命令、最近上下文和检查点入口会集中显示。",
                     button: "查看进度", destination: .workbench),
            HelpCard(id: "switch-agent", title: "切换 Agent",
                     description: "把当前任务移交给另一个 Agent，不丢上下文。",
                     answer: "先选择对话，再创建检查点或交接包。交接包会保留目标、关键文件、命令与来源证据。",
                     button: "打开待复核与交接", destination: .review),
            HelpCard(id: "remembered", title: "为什么没有被记住？",
                     description: "部分记忆建议需要确认后才会成为持久规则。",
                     answer: "AI Memory 将候选建议与已批准启动规则分开；请在“待复核”中批准、编辑、暂缓或拒绝。",
                     button: "打开待复核", destination: .review),
            HelpCard(id: "mcp", title: "为什么找不到 @aimemory？",
                     description: "AI Memory 通过 MCP 和后台采集工作，不依赖聊天中的 @ 提及。",
                     answer: "对 Agent 来说，AI Memory 是 MCP 工具集合；桌面应用负责人工恢复、检索、审批与迁移。",
                     button: "查看 Agent 集成", destination: .settings),
            HelpCard(id: "start", title: "我应该先从哪里开始？",
                     description: "除非正在审批内容，否则从“继续工作”开始最快。",
                     answer: "工作台提供最近任务、恢复入口、同步状态和下一步入口；历史页用于跨来源资料与产出。",
                     button: "去继续工作", destination: .workbench),
        ]
    }

    private var visibleCards: [HelpCard] {
        let term = query.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !term.isEmpty else { return cards }
        return cards.filter {
            [$0.title, $0.description, $0.answer].contains {
                $0.localizedCaseInsensitiveContains(term)
            }
        }
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                VStack(alignment: .leading, spacing: 6) {
                    Text("需要帮助？").font(Theme.appFont(size: 22, weight: .bold))
                    Text("先从最常见的问题开始。")
                        .font(Theme.appFont(size: 12))
                        .foregroundStyle(Theme.secondaryText)
                }
                .surfaceCard()

                TextField("搜索问题", text: $query)
                    .textFieldStyle(.roundedBorder)

                LazyVGrid(
                    columns: [
                        GridItem(.flexible(), spacing: 12),
                        GridItem(.flexible(), spacing: 12),
                    ],
                    spacing: 12
                ) {
                    ForEach(visibleCards) { card in
                        VStack(alignment: .leading, spacing: 8) {
                            Text(LocalizedStringKey(card.title))
                                .font(Theme.appFont(size: 13, weight: .semibold))
                            Text(LocalizedStringKey(card.description))
                                .font(Theme.appFont(size: 11))
                                .foregroundStyle(Theme.secondaryText)
                            Spacer()
                            Button(card.button) {
                                if card.id == "switch-agent" {
                                    store.toggleMemoryDrawer(tab: .continuation)
                                }
                                store.openWorkspace(card.destination)
                            }
                            .buttonStyle(.bordered)
                            .controlSize(.small)
                        }
                        .frame(maxWidth: .infinity, minHeight: 112, alignment: .leading)
                        .card()
                    }
                }

                VStack(alignment: .leading, spacing: 10) {
                    Text("后台工作方式")
                        .font(Theme.appFont(size: 15, weight: .semibold))
                    ForEach(visibleCards) { card in
                        VStack(alignment: .leading, spacing: 4) {
                            Text(LocalizedStringKey(card.title))
                                .font(Theme.appFont(size: 12, weight: .medium))
                            Text(LocalizedStringKey(card.answer))
                                .font(Theme.appFont(size: 11))
                                .foregroundStyle(Theme.secondaryText)
                        }
                        .padding(.vertical, 4)
                    }
                }
                .card()

                VStack(alignment: .leading, spacing: 10) {
                    Button {
                        advancedOpen.toggle()
                    } label: {
                        HStack {
                            Text("高级排障")
                            Spacer()
                            Image(systemName: advancedOpen ? "chevron.up" : "chevron.down")
                        }
                    }
                    .buttonStyle(.plain)
                    if advancedOpen {
                        helpMeta("当前来源", store.selectedAgent.label)
                        helpMeta("数据库", DataPaths.dbURL.path)
                        helpMeta("当前仓库", store.activeRepoRoot)
                        helpMeta(
                            "恢复命令",
                            store.selectedConversation?.resumeCommand ?? "当前对话没有恢复命令"
                        )
                    }
                }
                .card()
            }
            .padding(Theme.outerPadding)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .background(Theme.appBackground)
    }

    private func helpMeta(_ label: String, _ value: String) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(LocalizedStringKey(label))
                .font(Theme.appFont(size: 10))
                .foregroundStyle(Theme.mutedText)
            Text(value)
                .font(Theme.appFont(size: 10, design: .monospaced))
                .textSelection(.enabled)
        }
    }
}

private struct HelpCard: Identifiable {
    let id: String
    let title: String
    let description: String
    let answer: String
    let button: String
    let destination: WorkspaceDestination
}
