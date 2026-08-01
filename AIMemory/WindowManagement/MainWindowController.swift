// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
import AppKit
import SwiftUI

/// Helper for AppKit-only operations (NSOpenPanel for import) that need a
/// window reference. The main window itself is now owned by the SwiftUI
/// `WindowGroup` in `AIMemoryApp` (fixes the macOS 26 MainActor crash).
@MainActor
final class ImportPanelHelper {
    let store: AppStore

    init(store: AppStore) {
        self.store = store
    }

    /// The frontmost NSWindow (the SwiftUI-managed main window), if any.
    private var keyWindow: NSWindow? {
        NSApp.windows.first(where: { $0.isVisible && $0.contentView != nil })
    }

    func presentImportFromChatMem() {
        let panel = NSOpenPanel()
        panel.title = "选择 ChatMem 的 chatmem.db"
        panel.message = "AI Memory 会将此数据库复制到自己的独立数据目录，源文件不会被修改。"
        panel.allowedContentTypes = [.data, .item]
        panel.directoryURL = defaultChatMemDBURL()
        panel.allowsMultipleSelection = false
        panel.canChooseDirectories = false
        panel.canChooseFiles = true
        panel.prompt = "导入"

        let runImport: (URL) -> Void = { [weak self] url in
            self?.runImport(from: url)
        }

        if let window = keyWindow {
            panel.beginSheetModal(for: window) { response in
                guard response == .OK, let url = panel.url else { return }
                runImport(url)
            }
        } else {
            // Fallback: run modal without a sheet.
            let response = panel.runModal()
            if response == .OK, let url = panel.url {
                runImport(url)
            }
        }
    }

    private func defaultChatMemDBURL() -> URL? {
        let fm = FileManager.default
        if let support = fm.urls(for: .applicationSupportDirectory, in: .userDomainMask).first {
            let db = support.appendingPathComponent("ChatMem/chatmem.db")
            if fm.fileExists(atPath: db.path) { return db }
        }
        return fm.homeDirectoryForCurrentUser
    }

    private func runImport(from sourceURL: URL) {
        let store = self.store

        // Show an indeterminate progress sheet while the bridge copies.
        let progressVC = ImportProgressViewController(message: "正在从 ChatMem 导入数据…")
        if let window = keyWindow {
            window.contentViewController?.presentAsSheet(progressVC)
        }

        Task { @MainActor in
            do {
                let result = try await store.client.importFromChatMem(
                    sourceDBPath: sourceURL.path
                )
                progressVC.dismiss(nil)
                await store.reloadAllAgents()
                let summary = Self.formatImportReport(result, sourceURL: sourceURL)
                self.showImportResult(success: true, message: summary)
            } catch {
                progressVC.dismiss(nil)
                self.showImportResult(success: false, message: "导入失败：\(error.localizedDescription)")
            }
        }
    }

    private func showImportResult(success: Bool, message: String) {
        guard let window = keyWindow else {
            // Fallback to a banner if no window.
            store.bannerMessage = message
            return
        }
        let alert = NSAlert()
        alert.alertStyle = success ? .informational : .critical
        alert.messageText = success ? "导入完成" : "导入失败"
        alert.informativeText = message
        alert.addButton(withTitle: "好")
        alert.beginSheetModal(for: window)
    }

    private static func formatImportReport(_ result: [String: Any], sourceURL: URL) -> String {
        let bytes = (result["bytes"] as? Int) ?? 0
        let convs = (result["conversation_count"] as? Int) ?? 0
        let tables = (result["table_count"] as? Int) ?? 0
        let backup = (result["backup_path"] as? String)
        var lines: [String] = []
        lines.append("已复制 \(Self.byteString(bytes))，\(convs) 条对话，\(tables) 张表。")
        lines.append("源文件未被修改：\(sourceURL.lastPathComponent)")
        if let backup {
            lines.append("原 AI Memory 数据库已备份：\n\(backup)")
        }
        return lines.joined(separator: "\n")
    }

    private static func byteString(_ bytes: Int) -> String {
        let f = ByteCountFormatter()
        f.allowedUnits = [.useMB, .useKB]
        f.countStyle = .file
        return f.string(fromByteCount: Int64(bytes))
    }
}

// MARK: - Import progress sheet

final class ImportProgressViewController: NSViewController {
    private let message: String

    init(message: String) {
        self.message = message
        super.init(nibName: nil, bundle: nil)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError() }

    override func loadView() {
        let container = NSView(frame: NSRect(x: 0, y: 0, width: 320, height: 80))
        let label = NSTextField(labelWithString: message)
        label.font = .systemFont(ofSize: 12)
        label.translatesAutoresizingMaskIntoConstraints = false
        let spinner = NSProgressIndicator()
        spinner.style = .spinning
        spinner.isIndeterminate = true
        spinner.startAnimation(nil)
        spinner.translatesAutoresizingMaskIntoConstraints = false
        container.addSubview(label)
        container.addSubview(spinner)
        NSLayoutConstraint.activate([
            spinner.centerXAnchor.constraint(equalTo: container.centerXAnchor),
            spinner.topAnchor.constraint(equalTo: container.topAnchor, constant: 14),
            label.topAnchor.constraint(equalTo: spinner.bottomAnchor, constant: 10),
            label.centerXAnchor.constraint(equalTo: container.centerXAnchor),
        ])
        view = container
    }
}
