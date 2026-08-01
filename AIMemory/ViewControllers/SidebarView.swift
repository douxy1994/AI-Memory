// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
import SwiftUI

struct SidebarView: View {
    @ObservedObject var store: AppStore
    @State private var showMachineGroupManager = false
    @State private var showBulkTrashConfirm = false

    var body: some View {
        VStack(spacing: 0) {
            // Source picker + search.
            VStack(spacing: 8) {
                SourcePicker(store: store)
                SearchField(text: $store.searchQuery)
            }
            .padding(.horizontal, Theme.sidebarHPadding)
            .padding(.top, 10)
            .padding(.bottom, 8)

            // Library section header: 项目 + count + collapse/organize buttons.
            libraryHeader
                .padding(.horizontal, Theme.sidebarHPadding)
                .padding(.bottom, 8)

            // Conversation list (grouped by project or flat timeline).
            if store.projectGroups.isEmpty {
                emptyState
            } else {
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 2) {
                        if store.arrangeMode == .byProject || store.arrangeMode == .chatsFirst {
                            if store.hasMultipleMachines {
                                // Machine group layer.
                                ForEach(store.machineGroups) { mg in
                                    MachineGroupSection(machineGroup: mg, store: store)
                                }
                            } else {
                                // Project group layer (no machine groups needed).
                                ForEach(store.projectGroups) { group in
                                    ProjectGroupSection(group: group, store: store)
                                }
                            }
                        } else {
                            // Timeline: flat sorted list, no grouping.
                            ForEach(store.filteredConversations) { conv in
                                ConversationListRow(
                                    conv: conv,
                                    isSelected: conv.id == store.selectedConversationID,
                                    isFavorite: store.isFavorite(conv.id),
                                    isBulkSelectionMode: store.bulkSelectionMode,
                                    isBulkSelected: store.isBulkSelected(conv)
                                ) {
                                    if store.bulkSelectionMode {
                                        store.toggleBulkSelection(conv)
                                    } else {
                                        store.selectConversation(conv.id)
                                    }
                                } onToggleFavorite: {
                                    store.toggleFavorite(conv.id)
                                } onToggleBulk: {
                                    store.toggleBulkSelection(conv)
                                }
                            }
                        }
                    }
                    .padding(.horizontal, 8)
                    .padding(.bottom, 12)
                }
            }

            Divider().opacity(0.4)
            SidebarFooter(store: store)
        }
        .sheet(isPresented: $showMachineGroupManager) {
            MachineGroupManagerSheet(store: store)
        }
        .confirmationDialog(
            "将选中的 \(store.selectedConversationKeys.count) 条对话移入回收站？",
            isPresented: $showBulkTrashConfirm,
            titleVisibility: .visible
        ) {
            Button("移入回收站（可恢复）", role: .destructive) {
                Task { await store.trashBulkSelection() }
            }
            Button("取消", role: .cancel) {}
        } message: {
            Text("对话会保留可恢复快照 \(store.trashRetentionDays) 天，不会永久删除。")
        }
    }

    // MARK: - Library header (mirrors ChatMem library-section-header)

    private var libraryHeader: some View {
        HStack(spacing: 8) {
            SidebarGroupLabel(
                icon: "folder",
                title: "项目",
                count: store.projectGroups.count,
                titleWeight: .semibold,
                titleSize: 13,
                localizesTitle: true,
                countBackgroundOpacity: 0.14
            )

            Spacer()

            if store.bulkSelectionMode {
                Button("取消") { store.cancelBulkSelection() }
                    .buttonStyle(.borderless)
                    .controlSize(.small)
                Button("移入回收站 \(store.selectedConversationKeys.count)") {
                    showBulkTrashConfirm = true
                }
                .buttonStyle(.borderedProminent)
                .controlSize(.small)
                .disabled(store.selectedConversationKeys.isEmpty)
            } else {
                iconButton("checklist", help: "批量选择对话") {
                    store.bulkSelectionMode = true
                }
            }

            // Collapse all / restore all toggle.
            iconButton(
                store.allProjectsCollapsed ? "arrow.up.left.and.arrow.down.right" : "arrow.down.right.and.arrow.up.left",
                help: store.allProjectsCollapsed ? "展开全部项目" : "折叠全部项目"
            ) {
                if store.allProjectsCollapsed {
                    store.restoreAllProjects()
                } else {
                    store.collapseAllProjects()
                }
            }

            if store.hasMultipleMachines || !store.machineGroupOverrides.isEmpty {
                iconButton(
                    "rectangle.3.group",
                    help: "管理电脑分组"
                ) {
                    showMachineGroupManager = true
                }
            }

            // Organize menu (arrangement + sort + filters).
            Menu {
                Picker("排列", selection: $store.arrangeMode) {
                    Label("按项目", systemImage: "folder").tag(ArrangeMode.byProject)
                    Label("时间线", systemImage: "clock").tag(ArrangeMode.timeline)
                    Label("对话优先", systemImage: "bubble.left").tag(ArrangeMode.chatsFirst)
                }
                Picker("排序", selection: $store.sortMode) {
                    Label("最近更新", systemImage: "clock.arrow.circlepath").tag(SortMode.updatedDesc)
                    Label("最近创建", systemImage: "plus.circle").tag(SortMode.createdDesc)
                    Label("标题", systemImage: "textformat").tag(SortMode.titleAsc)
                }
                if !store.availableProjects.isEmpty {
                    Divider()
                    Menu("筛选项目") {
                        Button(store.projectFilters.isEmpty ? "全部项目 ✓" : "显示全部项目") {
                            store.projectFilters = []
                        }
                        Divider()
                        ForEach(store.availableProjects, id: \.self) { project in
                            let key = AppStore.projectPathKey(project)
                            Button {
                                if store.projectFilters.contains(key) {
                                    store.projectFilters.remove(key)
                                } else {
                                    store.projectFilters.insert(key)
                                }
                            } label: {
                                Text(
                                    "\(store.projectFilters.contains(key) ? "✓ " : "")\(AppStore.projectLabel(project))"
                                )
                            }
                        }
                    }
                }
            } label: {
                Image(systemName: "line.3.horizontal.decrease")
                    .font(Theme.appFont(size: 12))
                    .foregroundStyle(Theme.secondaryText)
                    .frame(width: 24, height: 24)
            }
            .menuStyle(.borderlessButton)
            .help("整理（排列 / 排序）")
        }
    }

    private func iconButton(_ icon: String, help: String, action: @escaping () -> Void) -> some View {
        Image(systemName: icon)
            .font(Theme.appFont(size: 12))
            .foregroundStyle(Theme.secondaryText)
            .frame(width: 24, height: 24)
            .contentShape(Rectangle())
            .onTapGesture(perform: action)
            .help(Text(LocalizedStringKey(help)))
    }

    private var emptyState: some View {
        VStack(spacing: 8) {
            Image(systemName: "tray")
                .font(Theme.appFont(size: 32, weight: .light))
                .foregroundStyle(Theme.mutedText)
            Text(store.searchQuery.isEmpty ? "未找到对话" : "无匹配对话")
                .font(Theme.appFont(size: 13))
                .foregroundStyle(Theme.secondaryText)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

// MARK: - Machine group management

private struct MachineGroupManagerSheet: View {
    @ObservedObject var store: AppStore
    @Environment(\.dismiss) private var dismiss
    @State private var names: [String: String] = [:]

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack {
                VStack(alignment: .leading, spacing: 3) {
                    Text("管理电脑分组")
                        .font(Theme.appFont(size: 17, weight: .semibold))
                    Text("重命名电脑、合并电脑，或把项目移动到另一个电脑分组。只改变 AI Memory 的展示分组，不修改会话原始路径。")
                        .font(Theme.appFont(size: 11))
                        .foregroundStyle(Theme.secondaryText)
                }
                Spacer()
                Button("取消") { dismiss() }
                    .keyboardShortcut(.cancelAction)
                Button("完成") { dismiss() }
                    .buttonStyle(.borderedProminent)
                    .keyboardShortcut(.defaultAction)
            }

            ScrollView {
                VStack(alignment: .leading, spacing: 10) {
                    ForEach(store.machineGroups) { group in
                        VStack(alignment: .leading, spacing: 8) {
                            HStack {
                                TextField(
                                    "电脑名称",
                                    text: Binding(
                                        get: { names[group.id] ?? group.label },
                                        set: { names[group.id] = $0 }
                                    )
                                )
                                .textFieldStyle(.roundedBorder)
                                Button("保存名称") {
                                    Task {
                                        await store.renameMachineGroup(
                                            id: group.id,
                                            label: names[group.id] ?? group.label
                                        )
                                    }
                                }
                                .buttonStyle(.bordered)
                                if store.machineGroups.count > 1 {
                                    Menu("合并至…") {
                                        ForEach(store.machineGroups.filter { $0.id != group.id }) {
                                            target in
                                            Button(target.label) {
                                                Task {
                                                    await store.mergeMachineGroup(
                                                        sourceID: group.id,
                                                        into: target.id
                                                    )
                                                }
                                            }
                                        }
                                    }
                                    .menuStyle(.borderlessButton)
                                }
                            }
                            ForEach(group.projects) { project in
                                HStack(spacing: 8) {
                                    VStack(alignment: .leading, spacing: 1) {
                                        Text(project.label)
                                            .font(Theme.appFont(size: 12, weight: .medium))
                                        Text(project.fullPath)
                                            .font(Theme.appFont(size: 9, design: .monospaced))
                                            .foregroundStyle(Theme.mutedText)
                                            .lineLimit(1)
                                            .truncationMode(.middle)
                                    }
                                    Spacer()
                                    Text("\(project.conversations.count) 条")
                                        .font(Theme.appFont(size: 10))
                                        .foregroundStyle(Theme.mutedText)
                                    if store.machineGroups.count > 1 {
                                        Menu("移动到…") {
                                            ForEach(
                                                store.machineGroups.filter { $0.id != group.id }
                                            ) { target in
                                                Button(target.label) {
                                                    Task {
                                                        await store.moveProjectToMachine(
                                                            projectPath: project.fullPath,
                                                            targetID: target.id
                                                        )
                                                    }
                                                }
                                            }
                                        }
                                        .menuStyle(.borderlessButton)
                                    }
                                }
                                .padding(8)
                                .background(Theme.soft)
                                .clipShape(RoundedRectangle(cornerRadius: 6))
                            }
                        }
                        .card()
                    }
                }
            }

            HStack {
                Button("重置分组") {
                    Task { await store.resetMachineGrouping() }
                }
                .buttonStyle(.bordered)
                .disabled(store.machineGroupOverrides.isEmpty)
                Spacer()
                Text("\(store.machineGroups.count) 个电脑分组")
                    .font(Theme.appFont(size: 11))
                    .foregroundStyle(Theme.mutedText)
            }
        }
        .padding(18)
        .frame(width: 650, height: 560)
        .onAppear {
            names = Dictionary(
                uniqueKeysWithValues: store.machineGroups.map {
                    ($0.id, store.machineGroupNames[$0.id] ?? $0.label)
                }
            )
        }
    }
}

// MARK: - Project group section (mirrors renderProjectGroup, App.tsx ~4088)

struct ProjectGroupSection: View {
    let group: ProjectGroup
    @ObservedObject var store: AppStore

    private var isExpanded: Bool { store.isProjectExpanded(group.id) }

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            // Group header: chevron + label + path + count pill.
            HStack(spacing: 6) {
                Image(systemName: "chevron.right")
                    .font(Theme.appFont(size: 10, weight: .semibold))
                    .foregroundStyle(Theme.secondaryText)
                    .rotationEffect(.degrees(isExpanded ? 90 : 0))
                    .frame(width: 12)

                VStack(alignment: .leading, spacing: 1) {
                    Text(group.label)
                        .font(Theme.appFont(size: 12, weight: .semibold))
                        .foregroundStyle(Theme.primaryText)
                        .lineLimit(1)
                    Text(group.fullPath)
                        .font(Theme.appFont(size: 9, design: .monospaced))
                        .foregroundStyle(Theme.mutedText)
                        .lineLimit(1)
                        .truncationMode(.middle)
                }

                Spacer()

                Text("\(group.conversations.count)")
                    .font(Theme.appFont(size: 10, weight: .semibold))
                    .foregroundStyle(Theme.accentStrong)
                    .padding(.horizontal, 6).padding(.vertical, 1)
                    .background(Theme.accent.opacity(0.12))
                    .clipShape(Capsule())
            }
            .padding(.horizontal, 8)
            .padding(.vertical, 6)
            .background(Theme.soft.opacity(0.5))
            .clipShape(RoundedRectangle(cornerRadius: 6))
            .contentShape(RoundedRectangle(cornerRadius: 6))
            .onTapGesture {
                store.toggleProjectExpanded(group.id)
            }

            // Expanded conversations.
            if isExpanded {
                VStack(alignment: .leading, spacing: 2) {
                    ForEach(group.conversations) { conv in
                        ConversationListRow(
                            conv: conv,
                            isSelected: conv.id == store.selectedConversationID,
                            isFavorite: store.isFavorite(conv.id),
                            isBulkSelectionMode: store.bulkSelectionMode,
                            isBulkSelected: store.isBulkSelected(conv)
                        ) {
                            if store.bulkSelectionMode {
                                store.toggleBulkSelection(conv)
                            } else {
                                store.selectConversation(conv.id)
                            }
                        } onToggleFavorite: {
                            store.toggleFavorite(conv.id)
                        } onToggleBulk: {
                            store.toggleBulkSelection(conv)
                        }
                    }
                }
                .padding(.leading, 14)
            }
        }
    }
}

// MARK: - Machine group section (mirrors machineGroups layer, App.tsx ~3320)

struct MachineGroupSection: View {
    let machineGroup: MachineGroup
    @ObservedObject var store: AppStore
    @State private var isExpanded = true

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            // Machine group header.
            HStack(spacing: 6) {
                SidebarGroupLabel(
                    icon: machineIcon,
                    title: machineGroup.label,
                    count: machineGroup.conversationCount,
                    titleWeight: .bold,
                    countBackgroundOpacity: 0.12
                )
                Spacer()
            }
            .padding(.horizontal, 8)
            .padding(.vertical, 6)
            .background(Theme.softStrong.opacity(0.5))
            .clipShape(RoundedRectangle(cornerRadius: 6))
            .contentShape(RoundedRectangle(cornerRadius: 6))
            .onTapGesture { isExpanded.toggle() }

            if isExpanded {
                VStack(alignment: .leading, spacing: 2) {
                    ForEach(machineGroup.projects) { pg in
                        ProjectGroupSection(group: pg, store: store)
                    }
                }
                .padding(.leading, 10)
            }
        }
    }

    private var machineIcon: String {
        switch machineGroup.id {
        case "windows": "pc"
        case "macos": "laptopcomputer"
        case "linux": "terminal"
        case "internal": "gearshape"
        default: "questionmark.folder"
        }
    }
}

private struct SidebarGroupLabel: View {
    let icon: String
    let title: String
    let count: Int
    let titleWeight: Font.Weight
    var titleSize: CGFloat = 12
    var localizesTitle = false
    let countBackgroundOpacity: Double

    var body: some View {
        HStack(spacing: 6) {
            Image(systemName: icon)
                .font(Theme.appFont(size: 11))
                .foregroundStyle(Theme.accent)
                .frame(width: 16, alignment: .center)

            Group {
                if localizesTitle {
                    Text(LocalizedStringKey(title))
                } else {
                    Text(verbatim: title)
                }
            }
                .font(Theme.appFont(size: titleSize, weight: titleWeight))
                .foregroundStyle(Theme.primaryText)
                .lineLimit(1)
                .frame(width: 70, alignment: .leading)

            Text("\(count)")
                .font(Theme.appFont(size: 10, weight: .semibold))
                .foregroundStyle(Theme.accentStrong)
                .frame(width: 24)
                .padding(.horizontal, 4)
                .padding(.vertical, 1)
                .background(Theme.accent.opacity(countBackgroundOpacity))
                .clipShape(Capsule())
        }
    }
}

// MARK: - Source picker

struct SourcePicker: View {
    @ObservedObject var store: AppStore

    var body: some View {
        Menu {
            ForEach(availableAgents, id: \.self) { agent in
                Button {
                    guard agent != store.selectedAgent else { return }
                    store.selectAgent(agent)
                } label: {
                    if agent == store.selectedAgent {
                        Label(agent.label, systemImage: "checkmark")
                    } else {
                        Text(agent.label)
                    }
                }
            }
        } label: {
            HStack(spacing: 8) {
                Image(systemName: "square.stack.3d.up.fill")
                    .font(Theme.appFont(size: 11, weight: .medium))
                    .foregroundStyle(Theme.accent)
                Text(store.selectedAgent.label)
                    .font(Theme.appFont(size: 13, weight: .semibold))
            }
            .frame(maxWidth: .infinity, minHeight: 38)
            .contentShape(RoundedRectangle(cornerRadius: 9, style: .continuous))
        }
        .menuStyle(.borderlessButton)
        .frame(maxWidth: .infinity)
        .padding(.vertical, 7)
        .background(Theme.surface.opacity(0.82))
        .overlay(
            RoundedRectangle(cornerRadius: 9, style: .continuous)
                .stroke(Theme.border.opacity(0.9), lineWidth: 1)
        )
        .clipShape(RoundedRectangle(cornerRadius: 9, style: .continuous))
        .shadow(color: Color.black.opacity(0.025), radius: 2, y: 1)
        .accessibilityLabel("来源：\(store.selectedAgent.label)")
    }

    private var availableAgents: [AgentKind] {
        var result = store.sources.compactMap { $0.agentKind }
        if !result.contains(store.selectedAgent) {
            result.insert(store.selectedAgent, at: 0)
        }
        if result.isEmpty {
            return AgentKind.allCases
        }
        return result
    }
}

// MARK: - Search

struct SearchField: View {
    @Binding var text: String

    var body: some View {
        HStack(spacing: 8) {
            Image(systemName: "magnifyingglass")
                .font(Theme.appFont(size: 12))
                .foregroundStyle(Theme.mutedText)
            TextField("搜索本地历史", text: $text)
                .textFieldStyle(.plain)
                .font(Theme.appFont(size: 14))
            if !text.isEmpty {
                Image(systemName: "xmark.circle.fill")
                    .font(Theme.appFont(size: 12))
                    .foregroundStyle(Theme.mutedText)
                    .contentShape(Rectangle())
                    .onTapGesture { text = "" }
            }
        }
        .padding(.horizontal, 14)
        .frame(minHeight: 42)
        .background(Theme.surface)
        .overlay(Capsule().stroke(Theme.border, lineWidth: 1))
        .clipShape(Capsule())
    }
}

// MARK: - Conversation row (mirrors renderConversationRow, App.tsx ~3981)
// Uses onTapGesture (NOT SwiftUI Button) to avoid the macOS 26.5.2 crash.

struct ConversationListRow: View {
    @Environment(\.locale) private var locale
    let conv: ConversationSummary
    let isSelected: Bool
    var isFavorite: Bool = false
    var isBulkSelectionMode: Bool = false
    var isBulkSelected: Bool = false
    let onTap: () -> Void
    var onToggleFavorite: (() -> Void)? = nil
    var onToggleBulk: (() -> Void)? = nil

    var body: some View {
        HStack(alignment: .top, spacing: 8) {
            if isBulkSelectionMode, let onToggleBulk {
                Image(systemName: isBulkSelected ? "checkmark.circle.fill" : "circle")
                    .font(Theme.appFont(size: 14))
                    .foregroundStyle(isBulkSelected ? Theme.accent : Theme.mutedText)
                    .frame(width: 18, height: 20)
                    .contentShape(Rectangle())
                    .onTapGesture { onToggleBulk() }
            }
            // Left accent bar (selected indicator).
            RoundedRectangle(cornerRadius: 2, style: .continuous)
                .fill(isSelected ? Theme.accent : Color.clear)
                .frame(width: 3)

            // Main content.
            VStack(alignment: .leading, spacing: 3) {
                HStack(spacing: 4) {
                    Text(conv.displayTitle)
                        .font(Theme.appFont(size: 12, weight: .medium))
                        .foregroundStyle(Theme.primaryText)
                        .lineLimit(2)
                        .multilineTextAlignment(.leading)
                    if isFavorite {
                        Image(systemName: "star.fill")
                            .font(Theme.appFont(size: 8))
                            .foregroundStyle(Theme.accent)
                    }
                    Spacer(minLength: 0)
                    // Timestamp (right-aligned).
                    Text(relativeTime(conv.updatedAt))
                        .font(Theme.appFont(size: 10))
                        .foregroundStyle(Theme.mutedText)
                }
                Text(conv.projectLeaf)
                    .font(Theme.appFont(size: 10))
                    .foregroundStyle(Theme.secondaryText)
                    .lineLimit(1)
                    .truncationMode(.middle)
            }
            .frame(maxWidth: .infinity, alignment: .leading)

            // Favorite star button (onTapGesture, not Button).
            if let onToggleFavorite {
                Image(systemName: isFavorite ? "star.fill" : "star")
                    .font(Theme.appFont(size: 11))
                    .foregroundStyle(isFavorite ? Theme.accent : Theme.mutedText.opacity(0.5))
                    .frame(width: 20, height: 20)
                    .contentShape(Rectangle())
                    .onTapGesture { onToggleFavorite() }
                    .help(
                        Text(
                            LocalizedStringKey(
                                isFavorite ? "取消收藏" : "加入收藏"
                            )
                        )
                    )
            }
        }
        .padding(.horizontal, 8)
        .padding(.vertical, 6)
        .background(isSelected ? Theme.selected : Color.clear)
        .clipShape(RoundedRectangle(cornerRadius: 6))
        .contentShape(RoundedRectangle(cornerRadius: 6))
        .onTapGesture { onTap() }
    }

    private func relativeTime(_ iso: String) -> String {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        let date = f.date(from: iso) ?? ISO8601DateFormatter().date(from: iso) ?? Date()
        let r = RelativeDateTimeFormatter()
        r.locale = locale
        r.unitsStyle = .short
        return r.localizedString(for: date, relativeTo: Date())
    }
}

// MARK: - Footer (mirrors ChatMem sidebar bottom entries)

struct SidebarFooter: View {
    @ObservedObject var store: AppStore

    var body: some View {
        HStack(spacing: 10) {
            footerButton("star", "收藏", dest: .favorites, count: store.favoriteConversations.count)
            footerButton("trash", "回收站", dest: .trash, count: store.trashed.count)
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 10)
    }

    private func footerButton(_ icon: String, _ label: String,
                               dest: WorkspaceDestination, count: Int) -> some View {
        let isActive = store.workspace == dest
        return HStack(spacing: 6) {
            Image(systemName: icon).font(Theme.appFont(size: 11))
            Text(LocalizedStringKey(label)).font(Theme.appFont(size: 11))
            Spacer(minLength: 4)
            Text("\(count)")
                .font(Theme.appFont(size: 9, weight: .semibold))
                .frame(minWidth: 22)
                .padding(.vertical, 1)
                .background(Theme.accent.opacity(0.16))
                .foregroundStyle(Theme.accentStrong)
                .clipShape(Capsule())
        }
        .frame(maxWidth: .infinity)
        .foregroundStyle(isActive ? Theme.accentStrong : Theme.secondaryText)
        .padding(.horizontal, 9).padding(.vertical, 6)
        .background(isActive ? Theme.selected : Color.clear)
        .clipShape(RoundedRectangle(cornerRadius: 7))
        .contentShape(RoundedRectangle(cornerRadius: 7))
        .onTapGesture { store.openWorkspace(dest) }
    }
}
