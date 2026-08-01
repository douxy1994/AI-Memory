// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
import SwiftUI

/// Right-side overlay drawer for repository rules management. Four tabs,
/// all wired to real bridge data via AppStore.
struct MemoryDrawerView: View {
    @ObservedObject var store: AppStore

    var body: some View {
        VStack(spacing: 0) {
            VStack(alignment: .leading, spacing: 4) {
                HStack {
                    VStack(alignment: .leading, spacing: 2) {
                        Text("记忆视图")
                            .font(Theme.appFont(size: 15, weight: .semibold))
                        Text("候选规则、已批准规则、Wiki 与继续工作状态。")
                            .font(Theme.appFont(size: 11))
                            .foregroundStyle(Theme.secondaryText)
                            .lineLimit(2)
                    }
                    Spacer()
                    Button(action: {store.toggleMemoryDrawer()}) {
                        Image(systemName: "xmark.circle.fill")
                            .font(Theme.appFont(size: 16))
                            .foregroundStyle(Theme.mutedText)
                    }
                    .buttonStyle(.borderless)
                }
                HStack(spacing: 6) {
                    Image(systemName: "folder").font(Theme.appFont(size: 9))
                    Text(store.activeRepoRoot)
                        .font(Theme.appFont(size: 10, design: .monospaced))
                        .lineLimit(1)
                        .truncationMode(.middle)
                    Spacer()
                    if store.memoryLoading {
                        ProgressView().controlSize(.mini)
                    }
                }
                .foregroundStyle(Theme.mutedText)
            }
            .padding(.horizontal, 18)
            .padding(.top, 18)
            .padding(.bottom, 12)

            drawerTabBar

            ScrollView {
                Group {
                    switch store.memoryDrawerTab {
                    case .review:
                        CandidateRulesTab(store: store)
                    case .rules:
                        ApprovedRulesTab(store: store)
                    case .wiki:
                        WikiTab(store: store)
                    case .continuation:
                        ContinuationTab(store: store)
                    }
                }
                .padding(.horizontal, 18)
                .padding(.bottom, 18)
            }
        }
    }

    /// A SwiftUI-native segmented control (avoids NSSegmentedControl, whose
    /// intrinsic-size measurement can crash when nested in an animated
    /// transition with @MainActor store access on macOS 26).
    private var drawerTabBar: some View {
        HStack(spacing: 4) {
            ForEach(MemoryDrawerTab.allCases) { tab in
                Button(action: {store.setMemoryDrawerTab(tab)}) {
                    Text(LocalizedStringKey(tab.label))
                        .font(Theme.appFont(size: 11, weight: store.memoryDrawerTab == tab ? .semibold : .regular))
                        .foregroundStyle(store.memoryDrawerTab == tab ? Theme.accentStrong : Theme.secondaryText)
                        .frame(maxWidth: .infinity)
                        .padding(.vertical, 5)
                        .background(store.memoryDrawerTab == tab ? Theme.accent.opacity(0.14) : Theme.soft)
                        .clipShape(RoundedRectangle(cornerRadius: 5))
                }
                .buttonStyle(.plain)
            }
        }
        .padding(.horizontal, 18)
        .padding(.bottom, 12)
    }
}

// MARK: - Candidate rules tab

struct CandidateRulesTab: View {
    @ObservedObject var store: AppStore

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Text("候选规则").font(Theme.appFont(size: 13, weight: .semibold))
                Spacer()
                Text("\(store.candidates.count) 条")
                    .font(Theme.appFont(size: 11))
                    .foregroundStyle(Theme.mutedText)
            }
            if store.candidates.isEmpty {
                EmptyNote("暂无候选规则。导入历史或运行扫描后，显式「Remember:/Rule:/Gotcha:」标记会生成候选。")
            } else {
                ForEach(store.candidates) { cand in
                    CandidateCard(candidate: cand, store: store)
                }
            }
        }
    }
}

struct CandidateCard: View {
    let candidate: MemoryCandidate
    @ObservedObject var store: AppStore
    @State private var expanded = false
    @State private var showApproveConfirm = false
    @State private var showEditor = false

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 6) {
                MemoryBadge.kind(candidate.kindLabel)
                MemoryBadge.status(candidate.statusLabel, ok: candidate.isActionable)
                Spacer()
                Text("置信 \(String(format: "%.2f", candidate.confidence))")
                    .font(Theme.appFont(size: 9))
                    .foregroundStyle(Theme.mutedText)
            }
            Text(candidate.summary)
                .font(Theme.appFont(size: 12, weight: .medium))
                .fixedSize(horizontal: false, vertical: true)
                .lineLimit(expanded ? nil : 2)
                .multilineTextAlignment(.leading)
            if expanded {
                Text(candidate.value)
                    .font(Theme.appFont(size: 11))
                    .foregroundStyle(Theme.secondaryText)
                    .fixedSize(horizontal: false, vertical: true)
                    .multilineTextAlignment(.leading)
                if !candidate.whyItMatters.isEmpty {
                    Text(candidate.whyItMatters)
                        .font(Theme.appFont(size: 10))
                        .foregroundStyle(Theme.mutedText)
                        .fixedSize(horizontal: false, vertical: true)
                        .multilineTextAlignment(.leading)
                }
                if !candidate.evidenceRefs.isEmpty {
                    VStack(alignment: .leading, spacing: 4) {
                        Text("证据")
                            .font(Theme.appFont(size: 10, weight: .semibold))
                        ForEach(Array(candidate.evidenceRefs.enumerated()), id: \.offset) { _, evidence in
                            Text(evidence.excerpt)
                                .font(Theme.appFont(size: 10))
                                .foregroundStyle(Theme.secondaryText)
                                .lineLimit(4)
                        }
                    }
                }
                if let merge = candidate.mergeSuggestion {
                    Label("合并建议：\(merge.preview)", systemImage: "arrow.triangle.merge")
                        .font(Theme.appFont(size: 10))
                        .foregroundStyle(Theme.secondaryText)
                        .lineLimit(4)
                }
                if let conflict = candidate.conflictSuggestion {
                    Label("冲突：\(conflict.preview)", systemImage: "exclamationmark.triangle")
                        .font(Theme.appFont(size: 10))
                        .foregroundStyle(Theme.danger)
                        .lineLimit(4)
                }
            }
            if candidate.isActionable {
                HStack(spacing: 6) {
                    Button("批准") { showApproveConfirm = true }
                        .buttonStyle(.borderedProminent)
                        .controlSize(.mini)
                    Button("编辑后批准") { showEditor = true }
                        .buttonStyle(.bordered)
                        .controlSize(.mini)
                    Button("拒绝") {
                        Task { await store.rejectCandidate(candidate) }
                    }
                    .buttonStyle(.bordered)
                    .controlSize(.mini)
                    Button("暂缓") {
                        Task { await store.snoozeCandidate(candidate) }
                    }
                    .buttonStyle(.bordered)
                    .controlSize(.mini)
                    Spacer()
                }
            }
            Button(expanded ? "收起" : "展开") { expanded.toggle() }
                .font(Theme.appFont(size: 10))
                .buttonStyle(.borderless)
        }
        .padding(10)
        .background(Theme.soft)
        .clipShape(RoundedRectangle(cornerRadius: 8))
        .confirmationDialog(
            "批准为启动规则？",
            isPresented: $showApproveConfirm,
            titleVisibility: .visible
        ) {
            Button("批准") {
                Task { await store.approveCandidate(candidate) }
            }
            Button("取消", role: .cancel) {}
        } message: {
            Text("批准后该规则会进入未来 agent 启动上下文。\n\n标题：\(candidate.summary)")
        }
        .sheet(isPresented: $showEditor) {
            CandidateEditorSheet(
                candidate: candidate,
                onSave: { title, value, usageHint in
                    showEditor = false
                    Task {
                        await store.approveCandidate(
                            candidate,
                            title: title,
                            value: value,
                            usageHint: usageHint
                        )
                    }
                },
                onCancel: { showEditor = false }
            )
        }
    }
}

private struct CandidateEditorSheet: View {
    let candidate: MemoryCandidate
    let onSave: (String, String, String) -> Void
    let onCancel: () -> Void
    @State private var title: String
    @State private var value: String
    @State private var usageHint = ""

    init(
        candidate: MemoryCandidate,
        onSave: @escaping (String, String, String) -> Void,
        onCancel: @escaping () -> Void
    ) {
        self.candidate = candidate
        self.onSave = onSave
        self.onCancel = onCancel
        _title = State(initialValue: candidate.summary)
        _value = State(initialValue: candidate.value)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("编辑后批准")
                .font(Theme.appFont(size: 18, weight: .semibold))
            TextField("规则标题", text: $title)
                .textFieldStyle(.roundedBorder)
            Text("规则内容").font(Theme.appFont(size: 11, weight: .medium))
            TextEditor(text: $value)
                .font(Theme.appFont(size: 12))
                .frame(minHeight: 150)
                .overlay(RoundedRectangle(cornerRadius: 6).stroke(Theme.border))
            TextField("使用提示（可选）", text: $usageHint)
                .textFieldStyle(.roundedBorder)
            HStack {
                Spacer()
                Button("取消", action: onCancel)
                    .keyboardShortcut(.cancelAction)
                Button("批准") {
                    onSave(
                        title.trimmingCharacters(in: .whitespacesAndNewlines),
                        value.trimmingCharacters(in: .whitespacesAndNewlines),
                        usageHint.trimmingCharacters(in: .whitespacesAndNewlines)
                    )
                }
                .buttonStyle(.borderedProminent)
                .keyboardShortcut(.defaultAction)
                .disabled(
                    title.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
                    || value.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
                )
            }
        }
        .padding(20)
        .frame(width: 520)
    }
}

// MARK: - Approved rules tab

struct ApprovedRulesTab: View {
    @ObservedObject var store: AppStore

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Text("已批准规则").font(Theme.appFont(size: 13, weight: .semibold))
                Spacer()
                Text("\(store.approvedMemories.count) 条")
                    .font(Theme.appFont(size: 11))
                    .foregroundStyle(Theme.mutedText)
            }
            if store.approvedMemories.isEmpty {
                EmptyNote("暂无已批准规则。审批候选后会出现在这里，并进入未来 agent 启动上下文。")
            } else {
                ForEach(store.approvedMemories) { mem in
                    ApprovedMemoryCard(memory: mem, store: store)
                }
            }
        }
    }
}

struct ApprovedMemoryCard: View {
    let memory: ApprovedMemory
    @ObservedObject var store: AppStore
    @State private var expanded = false
    @State private var showRetireConfirm = false

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 6) {
                MemoryBadge.kind(memory.kindLabel)
                MemoryBadge.freshness(memory.freshnessLabel)
                Spacer()
                if memory.freshnessScore > 0 {
                    Text(String(format: "%.0f%%", memory.freshnessScore * 100))
                        .font(Theme.appFont(size: 9))
                        .foregroundStyle(Theme.mutedText)
                }
            }
            Text(memory.title)
                .font(Theme.appFont(size: 12, weight: .medium))
                .fixedSize(horizontal: false, vertical: true)
                .lineLimit(expanded ? nil : 2)
                .multilineTextAlignment(.leading)
            if expanded {
                Text(memory.value)
                    .font(Theme.appFont(size: 11))
                    .foregroundStyle(Theme.secondaryText)
                    .fixedSize(horizontal: false, vertical: true)
                    .multilineTextAlignment(.leading)
                if !memory.usageHint.isEmpty {
                    Text("用法：" + memory.usageHint)
                        .font(Theme.appFont(size: 10))
                        .foregroundStyle(Theme.mutedText)
                        .fixedSize(horizontal: false, vertical: true)
                        .multilineTextAlignment(.leading)
                }
            }
            HStack(spacing: 6) {
                Button("确认仍有效") {
                    Task { await store.reverifyMemory(memory) }
                }
                .buttonStyle(.bordered).controlSize(.mini)
                Button("停用规则") {
                    showRetireConfirm = true
                }
                .buttonStyle(.bordered).controlSize(.mini)
                Spacer()
            }
            Button(expanded ? "收起" : "展开") { expanded.toggle() }
                .font(Theme.appFont(size: 10)).buttonStyle(.borderless)
        }
        .padding(10)
        .background(Theme.soft)
        .clipShape(RoundedRectangle(cornerRadius: 8))
        .confirmationDialog(
            "停用该规则？",
            isPresented: $showRetireConfirm,
            titleVisibility: .visible
        ) {
            Button("停用", role: .destructive) {
                Task { await store.retireMemory(memory) }
            }
            Button("取消", role: .cancel) {}
        } message: {
            Text("停用后该规则不会进入未来 agent 启动上下文（审计记录保留）。\n\n标题：\(memory.title)")
        }
    }
}

// MARK: - Wiki tab

struct WikiTab: View {
    @ObservedObject var store: AppStore

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Text("Wiki 投影").font(Theme.appFont(size: 13, weight: .semibold))
                Spacer()
                Text("\(store.wikiPages.count) 页")
                    .font(Theme.appFont(size: 11))
                    .foregroundStyle(Theme.mutedText)
            }
            if store.wikiPages.isEmpty {
                EmptyNote("暂无 Wiki。审批规则后可生成项目地图、模块地图、风险台账、命令、注意事项、最近工作等可读投影。")
            } else {
                ForEach(store.wikiPages) { page in
                    WikiPageCard(page: page)
                }
            }
        }
    }
}

struct WikiPageCard: View {
    let page: WikiPage
    @State private var expanded = false

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack(spacing: 6) {
                Image(systemName: "doc.text").font(Theme.appFont(size: 10)).foregroundStyle(Theme.accent)
                Text(page.title).font(Theme.appFont(size: 12, weight: .medium)).lineLimit(1)
                Spacer()
                Text(page.slug).font(Theme.appFont(size: 9, design: .monospaced)).foregroundStyle(Theme.mutedText)
            }
            if expanded {
                Text(page.body)
                    .font(Theme.appFont(size: 11))
                    .foregroundStyle(Theme.secondaryText)
                    .lineLimit(40)
                    .textSelection(.enabled)
            }
            Button(expanded ? "收起" : "展开") { expanded.toggle() }
                .font(Theme.appFont(size: 10)).buttonStyle(.borderless)
        }
        .padding(10)
        .background(Theme.soft)
        .clipShape(RoundedRectangle(cornerRadius: 8))
    }
}

// MARK: - Continuation tab (checkpoints + handoffs)

struct ContinuationTab: View {
    @ObservedObject var store: AppStore

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            VStack(alignment: .leading, spacing: 6) {
                HStack {
                    Text("检查点").font(Theme.appFont(size: 13, weight: .semibold))
                    Spacer()
                    Text("\(store.checkpoints.count) 个")
                        .font(Theme.appFont(size: 11))
                        .foregroundStyle(Theme.mutedText)
                }
                if store.checkpoints.isEmpty {
                    EmptyNote("暂无检查点。冻结当前上下文后会出现在这里。")
                } else {
                    ForEach(store.checkpoints.prefix(8)) { cp in
                        CheckpointRow(cp: cp, store: store)
                    }
                }
            }
            Divider()
            VStack(alignment: .leading, spacing: 6) {
                HStack {
                    Text("交接包").font(Theme.appFont(size: 13, weight: .semibold))
                    Spacer()
                    Menu {
                        ForEach(AgentKind.allCases) { agent in
                            Button(agent.label) {
                                Task { await store.createHandoff(toAgent: agent) }
                            }
                        }
                    } label: {
                        Image(systemName: "plus.circle")
                    }
                    .menuStyle(.borderlessButton)
                    .help("创建交接包")
                    Text("\(store.handoffs.count) 个")
                        .font(Theme.appFont(size: 11))
                        .foregroundStyle(Theme.mutedText)
                }
                if store.handoffs.isEmpty {
                    EmptyNote("暂无交接包。创建跨 agent 交接后会出现在这里。")
                } else {
                    ForEach(store.handoffs.prefix(8)) { hd in
                        HandoffRow(hd: hd, store: store)
                    }
                }
            }
        }
    }
}

struct CheckpointRow: View {
    let cp: Checkpoint
    @ObservedObject var store: AppStore

    var body: some View {
        HStack(alignment: .top, spacing: 8) {
            Image(systemName: "checkmark.seal").font(Theme.appFont(size: 11)).foregroundStyle(Theme.accent)
            VStack(alignment: .leading, spacing: 2) {
                Text(cp.summary).font(Theme.appFont(size: 11, weight: .medium)).lineLimit(2)
                HStack(spacing: 6) {
                    Text(cp.sourceAgent).font(Theme.appFont(size: 9)).foregroundStyle(Theme.mutedText)
                    if let mc = cp.messageCount {
                        Text("\(mc) 条消息").font(Theme.appFont(size: 9)).foregroundStyle(Theme.mutedText)
                    }
                    Text(cp.status).font(Theme.appFont(size: 9)).foregroundStyle(Theme.mutedText)
                }
            }
            Spacer()
            if cp.status == "active" {
                Menu {
                    ForEach(AgentKind.allCases) { agent in
                        Button(agent.label) {
                            Task {
                                await store.promoteCheckpoint(cp, toAgent: agent)
                            }
                        }
                    }
                } label: {
                    Image(systemName: "arrowshape.turn.up.right.circle")
                }
                .menuStyle(.borderlessButton)
                .help("提升为交接包")
            }
        }
        .padding(8)
        .background(Theme.soft)
        .clipShape(RoundedRectangle(cornerRadius: 6))
    }
}

struct HandoffRow: View {
    let hd: HandoffPacket
    @ObservedObject var store: AppStore
    @State private var showDetail = false

    var body: some View {
        HStack(alignment: .top, spacing: 8) {
            Image(systemName: "arrow.triangle.swap").font(Theme.appFont(size: 11)).foregroundStyle(Theme.accent)
            VStack(alignment: .leading, spacing: 2) {
                Text(hd.currentGoal).font(Theme.appFont(size: 11, weight: .medium)).lineLimit(2)
                Text("\(hd.fromAgent) → \(hd.toAgent) · \(hd.status)")
                    .font(Theme.appFont(size: 9))
                    .foregroundStyle(Theme.mutedText)
            }
            Spacer()
            Button {
                showDetail = true
            } label: {
                Image(systemName: "info.circle")
            }
            .buttonStyle(.borderless)
            .help("查看交接详情")
            if hd.status != "consumed" {
                Button {
                    Task { await store.consumeHandoff(hd) }
                } label: {
                    Image(systemName: "checkmark.circle")
                }
                .buttonStyle(.borderless)
                .help("标记为已消费")
            }
        }
        .padding(8)
        .background(Theme.soft)
        .clipShape(RoundedRectangle(cornerRadius: 6))
        .sheet(isPresented: $showDetail) {
            HandoffDetailSheet(handoff: hd, store: store)
        }
    }
}

// MARK: - Shared badge helpers

struct EmptyNote: View {
    let text: String
    init(_ text: String) { self.text = text }
    var body: some View {
        Text(LocalizedStringKey(text))
            .font(Theme.appFont(size: 11))
            .foregroundStyle(Theme.mutedText)
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(10)
            .background(Theme.surface)
            .clipShape(RoundedRectangle(cornerRadius: 6))
    }
}

enum MemoryBadge {
    static func kind(_ text: String) -> some View {
        Text(LocalizedStringKey(text))
            .font(Theme.appFont(size: 9, weight: .medium))
            .padding(.horizontal, 6).padding(.vertical, 1)
            .background(Theme.accent.opacity(0.16))
            .foregroundStyle(Theme.accentStrong)
            .fixedSize()
            .clipShape(Capsule())
    }
    static func status(_ text: String, ok: Bool) -> some View {
        Text(LocalizedStringKey(text))
            .font(Theme.appFont(size: 9, weight: .medium))
            .padding(.horizontal, 6).padding(.vertical, 1)
            .background(ok ? Theme.accent.opacity(0.16) : Theme.softStrong)
            .foregroundStyle(ok ? Theme.accentStrong : Theme.secondaryText)
            .fixedSize()
            .clipShape(Capsule())
    }
    static func freshness(_ text: String) -> some View {
        let ok = text == "有效"
        return Text(LocalizedStringKey(text))
            .font(Theme.appFont(size: 9, weight: .medium))
            .padding(.horizontal, 6).padding(.vertical, 1)
            .background(ok ? Theme.accent.opacity(0.16) : Theme.danger.opacity(0.16))
            .foregroundStyle(ok ? Theme.accentStrong : Theme.danger)
            .fixedSize()
            .clipShape(Capsule())
    }
}
