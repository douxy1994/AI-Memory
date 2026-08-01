// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
import SwiftUI

/// Review workspace: shows pending candidate rules, approved rules needing
/// re-verification, and draft handoffs — all for the active repo. Real data
/// from AppStore (loaded via bridge).
struct ReviewView: View {
    @ObservedObject var store: AppStore
    @State private var showRejectAllConfirm = false

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                header
                repoPicker
                metricsRow
                pendingSection
                conflictsSection
                approvedSection
                handoffSection
            }
            .padding(Theme.outerPadding)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .background(Theme.appBackground)
        .task {
            await store.loadRepoMemory(repoRoot: store.activeRepoRoot)
        }
    }

    private var header: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text("待复核").font(Theme.appFont(size: 22, weight: .bold))
            Text("审批候选规则、复核已批准规则、查看待复核的交接包。批准/拒绝/暂缓/停用已联动真实写入。")
                .font(Theme.appFont(size: 12))
                .foregroundStyle(Theme.secondaryText)
        }
        .surfaceCard()
    }

    private var repoPicker: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 10) {
                Image(systemName: "folder").foregroundStyle(Theme.accent)
                Text("当前仓库").font(Theme.appFont(size: 12, weight: .medium))
                TextField("仓库根目录", text: $store.activeRepoRoot)
                    .textFieldStyle(.roundedBorder)
                    .onSubmit {
                        let r = store.activeRepoRoot
                        Task { await store.loadRepoMemory(repoRoot: r) }
                    }
                Button("重新加载") {
                    let r = store.activeRepoRoot
                    Task { await store.loadRepoMemory(repoRoot: r) }
                }
                .buttonStyle(.bordered)
            }
            // Quick-pick repos that have pending candidates.
            if !store.reposWithCandidates.isEmpty {
                ScrollView(.horizontal, showsIndicators: false) {
                    HStack(spacing: 6) {
                        Text("有待审候选：").font(Theme.appFont(size: 10)).foregroundStyle(Theme.mutedText)
                        ForEach(store.reposWithCandidates) { repo in
                            Button(action: {Task {
                                    await store.setActiveRepo(repo.repoRoot)
                                }}) {
                                HStack(spacing: 4) {
                                    Text(repo.repoRoot).font(Theme.appFont(size: 10, design: .monospaced))
                                    Text("\(repo.pendingCount)")
                                        .font(Theme.appFont(size: 9, weight: .semibold))
                                        .padding(.horizontal, 4).padding(.vertical, 1)
                                        .background(Theme.accent.opacity(0.2))
                                        .foregroundStyle(Theme.accentStrong)
                                }
                                .padding(.horizontal, 8).padding(.vertical, 3)
                                .background(repo.repoRoot == store.activeRepoRoot ? Theme.selected : Theme.soft)
                                .clipShape(Capsule())
                            }
                            .buttonStyle(.plain)
                        }
                    }
                }
            }
        }
        .card()
    }

    private var metricsRow: some View {
        LazyVGrid(columns: [
            GridItem(.flexible(), spacing: 12),
            GridItem(.flexible(), spacing: 12),
            GridItem(.flexible(), spacing: 12),
        ], spacing: 12) {
            MetricTile(icon: "checklist",
                       label: "待审候选",
                       value: "\(store.pendingCandidates.count)")
            MetricTile(icon: "checkmark.seal",
                       label: "已批准规则",
                       value: "\(store.approvedMemories.count)")
            MetricTile(icon: "arrow.triangle.swap",
                       label: "交接包",
                       value: "\(store.handoffs.count)")
        }
    }

    private var pendingSection: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Text("待审候选规则").font(Theme.appFont(size: 15, weight: .semibold))
                Spacer()
                if !store.pendingCandidates.isEmpty {
                    Button("全部忽略") {
                        showRejectAllConfirm = true
                    }
                    .buttonStyle(.bordered)
                    .controlSize(.small)
                }
                Text("\(store.pendingCandidates.count) 条")
                    .font(Theme.appFont(size: 11))
                    .foregroundStyle(Theme.mutedText)
            }
            if store.pendingCandidates.isEmpty {
                EmptyNote("没有待审候选。导入历史后，显式「Remember:/Rule:/Gotcha:」标记会自动生成候选。")
            } else {
                ForEach(store.pendingCandidates) { cand in
                    CandidateCard(candidate: cand, store: store)
                }
            }
        }
        .card()
        .confirmationDialog(
            "忽略全部待审候选？",
            isPresented: $showRejectAllConfirm,
            titleVisibility: .visible
        ) {
            Button("忽略全部", role: .destructive) {
                Task { await store.rejectAllPendingCandidates() }
            }
            Button("取消", role: .cancel) {}
        } message: {
            Text("这些候选会标记为已拒绝，证据记录仍保留。")
        }
    }

    private var conflictsSection: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Text("记忆冲突").font(Theme.appFont(size: 15, weight: .semibold))
                Spacer()
                Text("\(store.memoryConflicts.count) 条")
                    .font(Theme.appFont(size: 11))
                    .foregroundStyle(Theme.mutedText)
            }
            if store.memoryConflicts.isEmpty {
                EmptyNote("当前没有需要处理的记忆冲突。")
            } else {
                ForEach(store.memoryConflicts) { conflict in
                    VStack(alignment: .leading, spacing: 5) {
                        HStack {
                            Label(conflict.memoryTitle, systemImage: "exclamationmark.triangle")
                                .font(Theme.appFont(size: 12, weight: .medium))
                                .foregroundStyle(Theme.danger)
                            Spacer()
                            Text(conflict.status)
                                .font(Theme.appFont(size: 9))
                                .foregroundStyle(Theme.mutedText)
                        }
                        Text(conflict.reason)
                            .font(Theme.appFont(size: 11))
                            .foregroundStyle(Theme.secondaryText)
                        Text("候选 \(conflict.candidateID) · 记忆 \(conflict.memoryID)")
                            .font(Theme.appFont(size: 9, design: .monospaced))
                            .foregroundStyle(Theme.mutedText)
                        HStack {
                            Spacer()
                            Button("查看相关规则") {
                                store.openMemoryDrawer(tab: .rules)
                            }
                            .buttonStyle(.bordered)
                            .controlSize(.small)
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

    private var approvedSection: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Text("已批准规则").font(Theme.appFont(size: 15, weight: .semibold))
                Spacer()
                Text("\(store.approvedMemories.count) 条")
                    .font(Theme.appFont(size: 11))
                    .foregroundStyle(Theme.mutedText)
            }
            if store.approvedMemories.isEmpty {
                EmptyNote("暂无已批准规则。")
            } else {
                ForEach(store.approvedMemories) { mem in
                    ApprovedMemoryCard(memory: mem, store: store)
                }
            }
        }
        .card()
    }

    private var handoffSection: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Text("交接包").font(Theme.appFont(size: 15, weight: .semibold))
                Spacer()
                Text("\(store.handoffs.count) 个")
                    .font(Theme.appFont(size: 11))
                    .foregroundStyle(Theme.mutedText)
            }
            if store.handoffs.isEmpty {
                EmptyNote("暂无交接包。")
            } else {
                ForEach(store.handoffs) { hd in
                    HandoffRow(hd: hd, store: store)
                }
            }
        }
        .card()
    }
}
