// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
import AppKit
import SwiftUI

@MainActor
enum AppBrandIcon {
    static let image: NSImage = {
        if let url = Bundle.main.url(forResource: "AppIcon", withExtension: "icns"),
           let image = NSImage(contentsOf: url) {
            return image
        }
        return NSApp.applicationIconImage
    }()
}

/// App entry point.
///
/// IMPORTANT: We use the SwiftUI `App` lifecycle (not manual
/// `NSApplication.shared.run()`). On macOS 26, manually bootstrapping
/// NSApplication leaves SwiftUI's MainActor executor unregistered, which
/// crashes every SwiftUI `Button` gesture via
/// `MainActor.assumeIsolated` → `swift_task_isMainExecutorImpl` → SIGSEGV.
/// The `App` protocol sets up the executor correctly.
@main
struct AIMemoryApp: App {
    @StateObject private var store: AppStore
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate

    init() {
        let client = BridgeClient()
        _store = StateObject(wrappedValue: AppStore(client: client))
    }

    var body: some Scene {
        WindowGroup("AI Memory") {
            RootView(store: store)
                .frame(
                    minWidth: 1040,
                    idealWidth: 1240,
                    minHeight: 680,
                    idealHeight: 820
                )
                .onAppear {
                    appDelegate.attach(store: store)
                    appDelegate.startBootstrap()
                }
        }
        .windowStyle(.hiddenTitleBar)
        .windowToolbarStyle(.unifiedCompact)
        .commands {
            CommandGroup(replacing: .appInfo) {
                Button("关于 AI Memory") {
                    NotificationCenter.default.post(
                        name: .requestOpenAboutWindow,
                        object: nil
                    )
                }
                Button("检查更新…") {
                    store.requestAboutUpdateCheck()
                    NotificationCenter.default.post(
                        name: .requestOpenAboutWindow,
                        object: nil
                    )
                }
            }

            CommandGroup(replacing: .appSettings) {
                Button("设置…") {
                    store.openWorkspace(.settings)
                }
                .keyboardShortcut(",")
            }

            // The custom macOS menu layout must still expose the standard
            // responder-chain editing commands. In particular,
            // NSSecureTextField relies on paste: reaching its field editor.
            CommandMenu("编辑") {
                Button("撤销") {
                    NSApp.sendAction(Selector(("undo:")), to: nil, from: nil)
                }
                .keyboardShortcut("z")

                Button("重做") {
                    NSApp.sendAction(Selector(("redo:")), to: nil, from: nil)
                }
                .keyboardShortcut("z", modifiers: [.command, .shift])

                Divider()

                Button("剪切") {
                    NSApp.sendAction(#selector(NSText.cut(_:)), to: nil, from: nil)
                }
                .keyboardShortcut("x")

                Button("复制") {
                    NSApp.sendAction(#selector(NSText.copy(_:)), to: nil, from: nil)
                }
                .keyboardShortcut("c")

                Button("粘贴") {
                    NSApp.sendAction(#selector(NSText.paste(_:)), to: nil, from: nil)
                }
                .keyboardShortcut("v")

                Button("全选") {
                    NSApp.sendAction(#selector(NSText.selectAll(_:)), to: nil, from: nil)
                }
                .keyboardShortcut("a")
            }

            CommandMenu("工作台") {
                Button("查看当前对话") {
                    guard let conversation = store.selectedSummary else { return }
                    store.selectConversation(conversation.id)
                }
                .disabled(store.selectedSummary == nil)

                Button("加载全部来源") {
                    Task { await store.loadAllAgentConversations() }
                }

                Divider()

                Button("待复核") {
                    store.openWorkspace(.review)
                }
                .keyboardShortcut("2")

                Divider()

                Button("打开/关闭记忆视图") {
                    store.toggleMemoryDrawer()
                }
                .keyboardShortcut("m", modifiers: [.command, .option])
            }
        }

        Window("关于 AI Memory", id: "about") {
            AboutView(store: store)
        }
        .defaultSize(width: 590, height: 700)
        .windowResizability(.contentSize)
        .windowStyle(.hiddenTitleBar)
    }
}

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    private var store: AppStore?
    private var importHelper: ImportPanelHelper?
    private var statusItem: NSStatusItem?
    private var bootstrapTask: Task<Void, Never>?
    private weak var retainedMainWindow: NSWindow?
    private var duplicateLaunchDetected = false

    func attach(store: AppStore) {
        self.store = store
        self.importHelper = ImportPanelHelper(store: store)
        DispatchQueue.main.async { [weak self] in
            self?.configureMainWindow()
        }
    }

    func startBootstrap() {
        guard bootstrapTask == nil else { return }
        bootstrapTask = Task { await bootstrap() }
    }

    func bootstrap() async {
        guard let store else {
            NSLog("[AIMemory] bootstrap: store is nil")
            return
        }
        await store.bootstrap()
    }

    func applicationWillFinishLaunching(_ notification: Notification) {
        let currentPID = ProcessInfo.processInfo.processIdentifier
        let instances = NSRunningApplication.runningApplications(
            withBundleIdentifier: Bundle.main.bundleIdentifier ?? "com.aimemory.app"
        )
        guard let owner = instances.min(by: {
            $0.processIdentifier < $1.processIdentifier
        }), owner.processIdentifier != currentPID else {
            return
        }

        // `LSMultipleInstancesProhibited` is the primary system-level guard.
        // This runtime fallback covers older LaunchServices behavior and
        // development copies carrying the same bundle identifier.
        duplicateLaunchDetected = true
        owner.activate(options: [.activateAllWindows, .activateIgnoringOtherApps])
        DispatchQueue.main.async {
            NSApp.terminate(nil)
        }
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        guard !duplicateLaunchDetected else { return }
        // Load the icon directly from this bundle. This avoids LaunchServices
        // showing a stale icon after a development build replaces AppIcon.icns.
        NSApp.applicationIconImage = AppBrandIcon.image
        NotificationCenter.default.addObserver(
            self,
            selector: #selector(importFromChatMemRequested(_:)),
            name: .requestImportFromChatMem,
            object: nil
        )
        NotificationCenter.default.addObserver(
            self,
            selector: #selector(interfaceLocaleChanged(_:)),
            name: .interfaceLocaleDidChange,
            object: nil
        )
        installMainMenu()
        installStatusItem()
    }

    // Pure AppKit menu — avoids SwiftUI ButtonAction crash on macOS 26.5.2.
    private func installMainMenu() {
        let english = store?.interfaceLocale.identifier.hasPrefix("en") == true
        let t: (String, String) -> String = { english ? $1 : $0 }
        let mainMenu = NSMenu()

        // App menu
        let appItem = NSMenuItem()
        let appMenu = NSMenu()
        appMenu.addItem(withTitle: t("关于 AI Memory", "About AI Memory"), action: #selector(showAbout), keyEquivalent: "")
        appMenu.addItem(withTitle: t("检查更新…", "Check for Updates…"), action: #selector(checkForUpdates), keyEquivalent: "")
        appMenu.addItem(NSMenuItem.separator())
        appMenu.addItem(withTitle: t("设置…", "Settings…"), action: #selector(openSettings), keyEquivalent: ",")
        appMenu.addItem(NSMenuItem.separator())
        let servicesItem = NSMenuItem(title: t("服务", "Services"), action: nil, keyEquivalent: "")
        servicesItem.submenu = NSMenu(title: t("服务", "Services"))
        appMenu.addItem(servicesItem)
        NSApp.servicesMenu = servicesItem.submenu
        appMenu.addItem(NSMenuItem.separator())
        appMenu.addItem(withTitle: t("隐藏 AI Memory", "Hide AI Memory"), action: #selector(NSApplication.hide(_:)), keyEquivalent: "h")
        appMenu.addItem(withTitle: t("隐藏其他", "Hide Others"), action: #selector(NSApplication.hideOtherApplications(_:)), keyEquivalent: "h").keyEquivalentModifierMask = [.command, .option]
        appMenu.addItem(withTitle: t("显示全部", "Show All"), action: #selector(NSApplication.unhideAllApplications(_:)), keyEquivalent: "")
        appMenu.addItem(NSMenuItem.separator())
        appMenu.addItem(withTitle: t("退出 AI Memory", "Quit AI Memory"), action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        appItem.submenu = appMenu
        appItem.title = "AI Memory"
        mainMenu.addItem(appItem)

        // File menu
        let fileItem = NSMenuItem()
        let fileMenu = NSMenu(title: t("文件", "File"))
        fileMenu.addItem(withTitle: t("从 ChatMem 导入…", "Import from ChatMem…"), action: #selector(doImport), keyEquivalent: "i")
        fileMenu.addItem(NSMenuItem.separator())
        fileMenu.addItem(withTitle: t("关闭窗口", "Close Window"), action: #selector(hideMainWindow), keyEquivalent: "w")
        fileItem.submenu = fileMenu
        fileItem.title = t("文件", "File")
        mainMenu.addItem(fileItem)

        // Edit menu (standard)
        let editItem = NSMenuItem()
        let editMenu = NSMenu(title: t("编辑", "Edit"))
        editMenu.addItem(withTitle: t("撤销", "Undo"), action: Selector(("undo:")), keyEquivalent: "z")
        editMenu.addItem(withTitle: t("重做", "Redo"), action: Selector(("redo:")), keyEquivalent: "z").keyEquivalentModifierMask = [.command, .shift]
        editMenu.addItem(NSMenuItem.separator())
        editMenu.addItem(withTitle: t("剪切", "Cut"), action: #selector(NSText.cut(_:)), keyEquivalent: "x")
        editMenu.addItem(withTitle: t("复制", "Copy"), action: #selector(NSText.copy(_:)), keyEquivalent: "c")
        editMenu.addItem(withTitle: t("粘贴", "Paste"), action: #selector(NSText.paste(_:)), keyEquivalent: "v")
        editMenu.addItem(withTitle: t("全选", "Select All"), action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")
        editItem.submenu = editMenu
        editItem.title = t("编辑", "Edit")
        mainMenu.addItem(editItem)

        // Workbench menu. Product actions live in the macOS menu bar rather
        // than occupying the window's title area.
        let workbenchItem = NSMenuItem()
        let workbenchMenu = NSMenu(title: t("工作台", "Workbench"))
        workbenchMenu.addItem(
            withTitle: t("查看当前对话", "View Current Conversation"),
            action: #selector(openSelectedConversation),
            keyEquivalent: ""
        )
        workbenchMenu.addItem(
            withTitle: t("加载全部来源", "Load All Sources"),
            action: #selector(loadAllSources),
            keyEquivalent: ""
        )
        workbenchMenu.addItem(NSMenuItem.separator())
        workbenchMenu.addItem(withTitle: t("待复核", "Needs Review"), action: #selector(goReview), keyEquivalent: "2")
        workbenchMenu.addItem(NSMenuItem.separator())
        let memoryItem = workbenchMenu.addItem(
            withTitle: t("打开/关闭记忆视图", "Toggle Memory View"),
            action: #selector(toggleMemoryDrawer),
            keyEquivalent: "m"
        )
        memoryItem.keyEquivalentModifierMask = [.command, .option]
        workbenchItem.submenu = workbenchMenu
        workbenchItem.title = t("工作台", "Workbench")
        mainMenu.addItem(workbenchItem)

        let windowItem = NSMenuItem()
        let windowMenu = NSMenu(title: t("窗口", "Window"))
        windowMenu.addItem(withTitle: t("最小化", "Minimize"), action: #selector(NSWindow.performMiniaturize(_:)), keyEquivalent: "m")
        windowMenu.addItem(withTitle: t("缩放", "Zoom"), action: #selector(NSWindow.performZoom(_:)), keyEquivalent: "")
        windowMenu.addItem(NSMenuItem.separator())
        windowMenu.addItem(withTitle: t("前置全部窗口", "Bring All to Front"), action: #selector(NSApplication.arrangeInFront(_:)), keyEquivalent: "")
        windowItem.submenu = windowMenu
        windowItem.title = t("窗口", "Window")
        mainMenu.addItem(windowItem)
        NSApp.windowsMenu = windowMenu

        let helpItem = NSMenuItem()
        let helpMenu = NSMenu(title: t("帮助", "Help"))
        helpMenu.addItem(withTitle: t("AI Memory 帮助", "AI Memory Help"), action: #selector(showHelp), keyEquivalent: "?")
        helpItem.submenu = helpMenu
        helpItem.title = t("帮助", "Help")
        mainMenu.addItem(helpItem)
        NSApp.helpMenu = helpMenu

        NSApp.mainMenu = mainMenu
    }

    private func installStatusItem() {
        if let statusItem {
            NSStatusBar.system.removeStatusItem(statusItem)
        }
        let english = store?.interfaceLocale.identifier.hasPrefix("en") == true
        let t: (String, String) -> String = { english ? $1 : $0 }
        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        item.button?.image = StatusBarIcon.make()
        item.button?.image?.isTemplate = true

        let menu = NSMenu()
        menu.addItem(withTitle: t("打开 AI Memory", "Open AI Memory"), action: #selector(showMainWindow), keyEquivalent: "")
        menu.addItem(withTitle: t("立即同步", "Sync Now"), action: #selector(syncFromStatusItem), keyEquivalent: "")
        menu.addItem(NSMenuItem.separator())
        menu.addItem(withTitle: t("退出 AI Memory", "Quit AI Memory"), action: #selector(NSApplication.terminate(_:)), keyEquivalent: "")
        item.menu = menu
        statusItem = item
    }

    private func configureMainWindow() {
        let appWindows = NSApp.windows.filter {
            $0.contentView != nil && !($0 is NSPanel)
        }
        guard let window = retainedMainWindow ?? appWindows.first else { return }
        retainedMainWindow = window
        window.isReleasedWhenClosed = false
        window.standardWindowButton(.closeButton)?.target = self
        window.standardWindowButton(.closeButton)?.action = #selector(hideMainWindow)

        // A WindowGroup remains the safest launch scene for the menu-bar app,
        // but AI Memory intentionally exposes one main window only.
        for duplicate in appWindows where duplicate !== window {
            duplicate.orderOut(nil)
            duplicate.close()
        }
    }

    private var mainWindow: NSWindow? {
        if let retainedMainWindow {
            return retainedMainWindow
        }
        let window = NSApp.windows.first { window in
            window.contentView != nil && !(window is NSPanel)
        }
        retainedMainWindow = window
        return window
    }

    @objc private func showAbout() {
        NSApp.activate(ignoringOtherApps: true)
        NotificationCenter.default.post(
            name: .requestOpenAboutWindow,
            object: nil
        )
    }
    @objc private func checkForUpdates() {
        NSApp.activate(ignoringOtherApps: true)
        store?.requestAboutUpdateCheck()
        NotificationCenter.default.post(
            name: .requestOpenAboutWindow,
            object: nil
        )
    }
    @objc private func doImport() { presentImportFromChatMem() }
    @objc private func goWorkbench() { store?.openWorkspace(.workbench) }
    @objc private func goReview() { store?.openWorkspace(.review) }
    @objc private func goHistory() { store?.openWorkspace(.history) }
    @objc private func toggleMemoryDrawer() { store?.toggleMemoryDrawer() }
    @objc private func openSelectedConversation() {
        guard let conversation = store?.selectedSummary else { return }
        store?.selectConversation(conversation.id)
    }
    @objc private func syncFromMenu() {
        Task { await store?.syncNow() }
    }
    @objc private func loadAllSources() {
        Task { await store?.loadAllAgentConversations() }
    }
    @objc private func refreshCurrentSource() {
        Task { await store?.reloadCurrentAgent() }
    }
    @objc private func openSettings() { store?.openWorkspace(.settings) }
    @objc private func showHelp() { store?.openWorkspace(.help) }

    @objc private func hideMainWindow() {
        mainWindow?.orderOut(nil)
    }

    @objc private func showMainWindow() {
        NSApp.activate(ignoringOtherApps: true)
        mainWindow?.makeKeyAndOrderFront(nil)
    }

    @objc private func syncFromStatusItem() {
        Task { await store?.syncNow() }
    }

    @objc private func importFromChatMemRequested(_ note: Notification) {
        presentImportFromChatMem()
    }

    @objc private func interfaceLocaleChanged(_ note: Notification) {
        installMainMenu()
        installStatusItem()
    }

    func presentImportFromChatMem() {
        importHelper?.presentImportFromChatMem()
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool {
        false
    }

    func applicationShouldHandleReopen(
        _ sender: NSApplication,
        hasVisibleWindows flag: Bool
    ) -> Bool {
        showMainWindow()
        return true
    }

    func applicationWillTerminate(_ notification: Notification) {
        bootstrapTask?.cancel()
    }
}

enum StatusBarIcon {
    static func make() -> NSImage {
        let size = NSSize(width: 18, height: 18)
        let configuration = NSImage.SymbolConfiguration(
            pointSize: 15,
            weight: .semibold
        )
        let head = NSImage(
            systemSymbolName: "brain.head.profile",
            accessibilityDescription: "AI Memory"
        )?.withSymbolConfiguration(configuration)

        let image = NSImage(size: size, flipped: false) { bounds in
            guard let head else { return false }
            let symbolSize = head.size
            let scale = min(
                (bounds.width - 2) / symbolSize.width,
                (bounds.height - 2) / symbolSize.height
            )
            let drawSize = NSSize(
                width: symbolSize.width * scale,
                height: symbolSize.height * scale
            )
            let drawRect = NSRect(
                x: bounds.midX - drawSize.width / 2,
                y: bounds.midY - drawSize.height / 2,
                width: drawSize.width,
                height: drawSize.height
            )
            head.draw(in: drawRect)
            return true
        }
        image.isTemplate = true
        image.accessibilityDescription = "AI Memory"
        return image
    }
}
