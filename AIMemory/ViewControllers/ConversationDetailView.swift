import AppKit
import SwiftUI

/// Full conversation view: header actions, meta strip, message transcript,
/// and a right rail with recovery command + file changes. Mirrors the React
/// `ConversationDetail.tsx` + workbench conversation layout.
struct ConversationDetailView: View {
    @ObservedObject var store: AppStore
    @State private var showTrashConfirm = false
    @State private var showMigrate = false

    var body: some View {
        Group {
            if let detail = store.selectedConversation {
                content(detail)
            } else if case .loading(let label) = store.loading, label == "读取对话详情…" {
                VStack(spacing: 12) {
                    ProgressView().controlSize(.large)
                    Text("读取对话详情…")
                        .font(Theme.appFont(size: 13))
                        .foregroundStyle(Theme.secondaryText)
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .background(Theme.appBackground)
            } else if let summary = store.selectedSummary {
                // Have summary but not detail yet — show header with a hint.
                VStack(spacing: 10) {
                    Image(systemName: "doc.text")
                        .font(Theme.appFont(size: 32, weight: .light))
                        .foregroundStyle(Theme.mutedText)
                    Text(summary.displayTitle).font(Theme.appFont(size: 14, weight: .medium))
                    Button("读取详情") {
                        store.selectConversation(summary.id)
                    }
                    .buttonStyle(.bordered)
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .background(Theme.appBackground)
            } else {
                TextPlaceholderView(icon: "bubble.left",
                                    title: "选择一个对话",
                                    message: "从左侧选择对话以查看完整内容。")
            }
        }
    }

    private func content(_ detail: ConversationDetail) -> some View {
        HStack(spacing: 0) {
            ScrollView {
                VStack(alignment: .leading, spacing: 16) {
                    header(detail)
                    metaStrip(detail)
                    statsRow(detail)
                    messagesList(detail)
                }
                .padding(Theme.outerPadding)
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            .frame(maxWidth: .infinity)
            recoveryRail(detail)
                .frame(width: Theme.recoveryRailWidth)
                .background(Theme.soft)
                .overlay(Rectangle().frame(width: 1).foregroundColor(Theme.border), alignment: .leading)
        }
        .background(Theme.appBackground)
        .confirmationDialog(
            "移入回收站？",
            isPresented: $showTrashConfirm,
            titleVisibility: .visible
        ) {
            Button("移入回收站（可恢复）", role: .destructive) {
                if let id = store.selectedConversationID {
                    Task {
                        await store.trashConversation(agent: store.selectedAgent.rawValue, id: id)
                    }
                    store.openWorkspace(.workbench)
                }
            }
            Button("取消", role: .cancel) {}
        } message: {
            Text("该对话会从 \(store.selectedAgent.label) 本地存储移除，并保留可恢复快照 \(store.trashRetentionDays) 天。")
        }
        .sheet(isPresented: $showMigrate) {
            MigrateSheet(store: store, sourceAgent: store.selectedAgent)
        }
    }

    // MARK: - Header

    private func header(_ detail: ConversationDetail) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(detail.displayTitle)
                .font(Theme.appFont(size: 18, weight: .semibold))
                .lineLimit(3)
                .fixedSize(horizontal: false, vertical: true)
                .frame(maxWidth: .infinity, alignment: .leading)
            // Action chips wrap to their own row so a long title doesn't
            // starve them of width.
            ActionFlowLayout(spacing: 6) {
                chipButton("迁移", "arrow.triangle.swap") { showMigrate = true }
                chipButton("删除", "trash") { showTrashConfirm = true }
                chipButton(
                    store.isFavorite(detail.id) ? "取消收藏" : "收藏",
                    store.isFavorite(detail.id) ? "star.fill" : "star"
                ) {
                    store.toggleFavorite(detail.id)
                }
                chipButton("路径", "folder") { copyText(detail.projectDir) }
                if let cmd = detail.resumeCommand {
                    chipButton("恢复", "play.circle") { copyText(cmd) }
                }
                chipButton("检查点", "checkmark.seal") {
                    Task { await store.createCheckpointForSelectedConversation() }
                }
                chipButton("记忆", "sidebar.right") {
                    store.toggleMemoryDrawer()
                }
            }
            if let summary = detail.summary, !summary.isEmpty {
                Text(summary)
                    .font(Theme.appFont(size: 12))
                    .foregroundStyle(Theme.secondaryText)
                    .lineLimit(3)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
        .surfaceCard()
    }

    private func chipButton(_ label: String, _ icon: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Label(LocalizedStringKey(label), systemImage: icon)
                .font(Theme.appFont(size: 11))
        }
        .buttonStyle(.bordered)
        .controlSize(.small)
        .fixedSize()
    }

    // MARK: - Meta

    private func metaStrip(_ detail: ConversationDetail) -> some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: 16) {
                metaItem("项目", detail.projectLeaf, icon: "folder")
                metaItem("来源", store.selectedAgent.label, icon: "cpu")
                metaItem("更新", shortDate(detail.updatedAt), icon: "clock")
                if detail.resumeCommand != nil {
                    metaItem("恢复命令", "可用", icon: "play.circle")
                }
            }
        }
        .padding(12)
        .background(Theme.surface)
        .overlay(RoundedRectangle(cornerRadius: 8).stroke(Theme.border, lineWidth: 1))
        .clipShape(RoundedRectangle(cornerRadius: 8))
    }

    private func metaItem(_ label: String, _ value: String, icon: String) -> some View {
        HStack(spacing: 6) {
            Image(systemName: icon).font(Theme.appFont(size: 11)).foregroundStyle(Theme.mutedText)
            VStack(alignment: .leading, spacing: 1) {
                Text(LocalizedStringKey(label))
                    .font(Theme.appFont(size: 10))
                    .foregroundStyle(Theme.mutedText)
                Text(value).font(Theme.appFont(size: 12, weight: .medium)).lineLimit(1)
            }
        }
    }

    // MARK: - Stats

    private func statsRow(_ detail: ConversationDetail) -> some View {
        HStack(spacing: 10) {
            statBlock("\(detail.messages.count)", "消息")
            statBlock("\(detail.fileChanges.count)", "文件变更")
            statBlock("\(detail.messages.reduce(0) { $0 + $1.toolCalls.count })", "工具调用")
        }
    }

    private func statBlock(_ value: String, _ label: String) -> some View {
        HStack(spacing: 6) {
            Text(value).font(Theme.appFont(size: 13, weight: .semibold))
            Text(LocalizedStringKey(label))
                .font(Theme.appFont(size: 11))
                .foregroundStyle(Theme.secondaryText)
        }
        .padding(.horizontal, 10).padding(.vertical, 6)
        .background(Theme.soft)
        .fixedSize()
        .clipShape(Capsule())
    }

    // MARK: - Transcript

    private func messagesList(_ detail: ConversationDetail) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("对话内容").font(Theme.appFont(size: 15, weight: .semibold))
            ForEach(detail.messages) { msg in
                MessageRow(message: msg)
            }
        }
        .card(padding: 16)
    }

    // MARK: - Recovery rail

    private func recoveryRail(_ detail: ConversationDetail) -> some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 14) {
                Text("可恢复进度").font(Theme.appFont(size: 13, weight: .semibold))
                if let cmd = detail.resumeCommand {
                    VStack(alignment: .leading, spacing: 6) {
                        Text("恢复命令")
                            .font(Theme.appFont(size: 11))
                            .foregroundStyle(Theme.mutedText)
                        Text(cmd)
                            .font(Theme.appFont(size: 11, design: .monospaced))
                            .padding(8)
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .background(Theme.surface)
                            .overlay(RoundedRectangle(cornerRadius: 6).stroke(Theme.border, lineWidth: 1))
                            .clipShape(RoundedRectangle(cornerRadius: 6))
                            .contextMenu {
                                Button("复制") { copyText(cmd) }
                            }
                        Button(action: { copyText(cmd) }) {
                            Label("复制恢复命令", systemImage: "doc.on.doc")
                                .font(Theme.appFont(size: 11))
                        }
                    }
                }
                if !detail.fileChanges.isEmpty {
                    VStack(alignment: .leading, spacing: 6) {
                        Text("文件变更 (\(detail.fileChanges.count))")
                            .font(Theme.appFont(size: 11))
                            .foregroundStyle(Theme.mutedText)
                        ForEach(detail.fileChanges) { fc in
                            FileChangeRow(fc: fc)
                        }
                    }
                }
                if let sp = detail.storagePath {
                    VStack(alignment: .leading, spacing: 6) {
                        Text("存储位置")
                            .font(Theme.appFont(size: 11))
                            .foregroundStyle(Theme.mutedText)
                        Text(sp)
                            .font(Theme.appFont(size: 10, design: .monospaced))
                            .foregroundStyle(Theme.secondaryText)
                            .lineLimit(3)
                            .frame(maxWidth: .infinity, alignment: .leading)
                    }
                }
                Spacer()
            }
            .padding(14)
        }
    }

    // MARK: - Helpers

    private func shortDate(_ iso: String) -> String {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        let date = f.date(from: iso) ?? ISO8601DateFormatter().date(from: iso) ?? Date()
        let df = DateFormatter()
        df.dateStyle = .short
        df.timeStyle = .short
        return df.string(from: date)
    }

    private func copyText(_ s: String) {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(s, forType: .string)
        store.flash("已复制到剪贴板")
    }

}

// MARK: - Message row

private struct MessageRow: View {
    let message: ConversationMessage
    @State private var expanded = false

    private var isUser: Bool { message.role.lowercased() == "user" }
    private var isLongAssistantMessage: Bool {
        !isUser && (message.content.count > 800 || message.content.split(separator: "\n").count > 12)
    }
    private var renderedContent: AttributedString {
        (try? AttributedString(
            markdown: message.content,
            options: .init(interpretedSyntax: .full)
        )) ?? AttributedString(message.content)
    }

    var body: some View {
        HStack(alignment: .top, spacing: 10) {
            VStack(spacing: 2) {
                Circle()
                    .fill(isUser ? Theme.accent : Theme.softStrong)
                    .frame(width: 8, height: 8)
                .padding(.top, 5)
            }
            VStack(alignment: .leading, spacing: 6) {
                HStack(spacing: 6) {
                    Text(message.roleLabel)
                        .font(Theme.appFont(size: 11, weight: .semibold))
                        .foregroundStyle(isUser ? Theme.accentStrong : Theme.secondaryText)
                    if !message.toolCalls.isEmpty {
                        Text("\(message.toolCalls.count) 工具调用")
                            .font(Theme.appFont(size: 10))
                            .foregroundStyle(Theme.mutedText)
                    }
                }
                if !message.content.isEmpty {
                    Text(renderedContent)
                        .font(Theme.appFont(size: 12))
                        .lineLimit(isLongAssistantMessage && !expanded ? 12 : nil)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .textSelection(.enabled)
                    if isLongAssistantMessage {
                        Button(expanded ? "收起" : "展开全文") { expanded.toggle() }
                            .font(Theme.appFont(size: 10))
                            .buttonStyle(.borderless)
                    }
                }
                if !message.toolCalls.isEmpty {
                    ToolCallsBlock(toolCalls: message.toolCalls)
                }
            }
            .padding(10)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(isUser ? Theme.accent.opacity(0.06) : Theme.soft)
            .clipShape(RoundedRectangle(cornerRadius: 8))
        }
    }
}

private struct ToolCallsBlock: View {
    let toolCalls: [ToolCall]
    @State private var expanded = false

    var body: some View {
        DisclosureGroup(isExpanded: $expanded) {
            VStack(alignment: .leading, spacing: 4) {
                ForEach(toolCalls) { call in
                    HStack(spacing: 6) {
                        Image(systemName: statusIcon(call.status))
                            .font(Theme.appFont(size: 9))
                            .foregroundStyle(call.status.lowercased() == "success" ? Theme.accent : Theme.danger)
                        Text(call.name)
                            .font(Theme.appFont(size: 11, design: .monospaced))
                        Text(call.input.preview)
                            .font(Theme.appFont(size: 10, design: .monospaced))
                            .foregroundStyle(Theme.mutedText)
                            .lineLimit(1)
                        Spacer()
                        statusBadge(call.status)
                    }
                    .padding(.horizontal, 8).padding(.vertical, 4)
                    .background(Theme.surface)
                    .clipShape(RoundedRectangle(cornerRadius: 5))
                }
            }
        } label: {
            Text("展开工具详情")
                .font(Theme.appFont(size: 10))
                .foregroundStyle(Theme.secondaryText)
        }
    }

    private func statusIcon(_ s: String) -> String {
        s.lowercased() == "success" ? "checkmark.circle.fill" : "exclamationmark.triangle.fill"
    }

    private func statusBadge(_ s: String) -> some View {
        let ok = s.lowercased() == "success"
        return Text(ok ? "成功" : "异常")
            .font(Theme.appFont(size: 9, weight: .medium))
            .padding(.horizontal, 6).padding(.vertical, 1)
            .background(ok ? Theme.accent.opacity(0.16) : Theme.danger.opacity(0.16))
            .foregroundStyle(ok ? Theme.accentStrong : Theme.danger)
            .fixedSize()
            .clipShape(Capsule())
    }
}

private struct FileChangeRow: View {
    let fc: FileChange

    var body: some View {
        HStack(spacing: 6) {
            Image(systemName: fc.changeIcon)
                .font(Theme.appFont(size: 9))
                .foregroundStyle(Theme.mutedText)
            Text(fc.path)
                .font(Theme.appFont(size: 10, design: .monospaced))
                .lineLimit(1)
                .truncationMode(.middle)
            Spacer()
            Text(fc.changeTypeLabel)
                .font(Theme.appFont(size: 9))
                .foregroundStyle(Theme.secondaryText)
        }
        .padding(.horizontal, 6).padding(.vertical, 3)
        .background(Theme.surface)
        .clipShape(RoundedRectangle(cornerRadius: 5))
    }
}

/// Keeps action buttons at their intrinsic width and wraps whole controls
/// instead of compressing their labels when the detail pane becomes narrow.
private struct ActionFlowLayout: Layout {
    let spacing: CGFloat

    func sizeThatFits(
        proposal: ProposedViewSize,
        subviews: Subviews,
        cache: inout ()
    ) -> CGSize {
        let availableWidth = proposal.width ?? .greatestFiniteMagnitude
        var rowWidth: CGFloat = 0
        var rowHeight: CGFloat = 0
        var contentWidth: CGFloat = 0
        var contentHeight: CGFloat = 0

        for subview in subviews {
            let size = subview.sizeThatFits(.unspecified)
            if rowWidth > 0, rowWidth + spacing + size.width > availableWidth {
                contentWidth = max(contentWidth, rowWidth)
                contentHeight += rowHeight + spacing
                rowWidth = size.width
                rowHeight = size.height
            } else {
                rowWidth += (rowWidth == 0 ? 0 : spacing) + size.width
                rowHeight = max(rowHeight, size.height)
            }
        }

        return CGSize(
            width: proposal.width ?? max(contentWidth, rowWidth),
            height: contentHeight + rowHeight
        )
    }

    func placeSubviews(
        in bounds: CGRect,
        proposal: ProposedViewSize,
        subviews: Subviews,
        cache: inout ()
    ) {
        var x = bounds.minX
        var y = bounds.minY
        var rowHeight: CGFloat = 0

        for subview in subviews {
            let size = subview.sizeThatFits(.unspecified)
            if x > bounds.minX, x + size.width > bounds.maxX {
                x = bounds.minX
                y += rowHeight + spacing
                rowHeight = 0
            }
            subview.place(
                at: CGPoint(x: x, y: y),
                anchor: .topLeading,
                proposal: ProposedViewSize(size)
            )
            x += size.width + spacing
            rowHeight = max(rowHeight, size.height)
        }
    }
}

private extension ConversationDetail {
    var displayTitle: String {
        if let summary, !summary.isEmpty { return String(summary.prefix(100)) }
        let leaf = (projectDir.trimmingCharacters(in: .init(charactersIn: "/")) as NSString).lastPathComponent
        return leaf.isEmpty ? id : leaf
    }
}

extension ConversationDetail {
    /// Project directory leaf name, for compact display.
    var projectLeaf: String {
        let trimmed = projectDir.trimmingCharacters(in: .init(charactersIn: "/"))
        let leaf = (trimmed as NSString).lastPathComponent
        return leaf.isEmpty ? projectDir : leaf
    }
}

// MARK: - Migrate sheet

struct MigrateSheet: View {
    @ObservedObject var store: AppStore
    let sourceAgent: AgentKind
    @State private var target: AgentKind = .codex
    @State private var mode: String = "copy"
    @State private var kind: String = "full"
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("迁移对话").font(Theme.appFont(size: 15, weight: .semibold))
            Text("可在本机已识别的 Agent 之间完整迁移，也可只复制低 token 的继续卡片。完整迁移写入后会回读验证，验证失败时不会删除源对话。")
                .font(Theme.appFont(size: 11))
                .foregroundStyle(Theme.mutedText)

            Picker("迁移内容", selection: $kind) {
                Text("完整对话迁移").tag("full")
                Text("总结式迁移").tag("brief")
            }
            .pickerStyle(.radioGroup)

            if kind == "full" {
                HStack(spacing: 8) {
                    Picker("源", selection: .constant(sourceAgent)) {
                        Text(sourceAgent.label).tag(sourceAgent)
                    }.labelsHidden().disabled(true)
                    Image(systemName: "arrow.right").foregroundStyle(Theme.accent)
                    Picker("目标", selection: $target) {
                        ForEach(
                            AgentKind.allCases.filter {
                                $0 != sourceAgent && $0.supportsNativeMigrationTarget
                            }
                        ) { agent in
                            Text(agent.label).tag(agent)
                        }
                    }
                }
                Picker("迁移方式", selection: $mode) {
                    Text("复制（保留源）").tag("copy")
                    Text("移动（验证后将源移入回收站）").tag("cut")
                }
                .pickerStyle(.radioGroup)
            } else {
                Text("总结式迁移不会写入目标 Agent，也不会删除原对话；它只把来源明确、可按需展开证据的继续卡片复制到剪贴板。")
                    .font(Theme.appFont(size: 11))
                    .foregroundStyle(Theme.secondaryText)
                    .padding(10)
                    .background(Theme.soft)
                    .clipShape(RoundedRectangle(cornerRadius: 7))
            }
            HStack {
                Spacer()
                Button("取消") { dismiss() }
                Button(kind == "brief" ? "复制继续卡片" : mode == "copy" ? "复制" : "移动") {
                    Task {
                        if kind == "brief", let detail = store.selectedConversation {
                            let brief = store.continuationBrief(for: detail)
                            NSPasteboard.general.clearContents()
                            NSPasteboard.general.setString(brief, forType: .string)
                            store.flash("继续卡片已复制。")
                            dismiss()
                        } else if let id = store.selectedConversationID {
                            await store.migrateConversation(
                                source: sourceAgent.rawValue,
                                target: target.rawValue,
                                id: id,
                                mode: mode
                            )
                            dismiss()
                        }
                    }
                }
                .buttonStyle(.borderedProminent)
                .disabled(store.selectedConversation == nil)
            }
        }
        .padding(20)
        .frame(width: 470)
        .onAppear {
            if target == sourceAgent {
                target = AgentKind.allCases.first {
                    $0 != sourceAgent && $0.supportsNativeMigrationTarget
                } ?? .codex
            }
        }
    }
}
