// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
import AppKit
import SwiftUI

/// Top-level app shell: topbar (brand + status) + sidebar + workspace,
/// with an optional right-side memory drawer overlay.
struct RootView: View {
    @ObservedObject var store: AppStore
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        ZStack {
            VStack(spacing: 0) {
                topbar
                HStack(spacing: 0) {
                    if store.workspace != .settings {
                        SidebarView(store: store)
                            .frame(width: Theme.sidebarWidth)
                            .background(Theme.sidebarBackground)
                            .overlay(
                                Rectangle()
                                    .frame(width: 1)
                                    .foregroundColor(Theme.border),
                                alignment: .trailing
                            )
                    }
                    WorkspaceRouter(store: store)
                        .frame(maxWidth: .infinity, maxHeight: .infinity)
                        .background(Theme.appBackground)
                }
            }
            if store.memoryDrawerOpen {
                HStack(spacing: 0) {
                    // Invisible tap-to-dismiss scrim over the workspace area.
                    Color.black.opacity(0.01)
                        .onTapGesture { store.toggleMemoryDrawer() }
                    MemoryDrawerView(store: store)
                        .frame(width: Theme.drawerWidth)
                        .background(Theme.surface)
                        .overlay(
                            Rectangle().frame(width: 1).foregroundColor(Theme.border),
                            alignment: .leading
                        )
                        .shadow(color: Color.black.opacity(0.18), radius: 30, x: -10, y: 0)
                        .transition(.move(edge: .trailing))
                }
            }
        }
        .background(Theme.appBackground)
        .overlay(alignment: .top) {
            if let banner = store.bannerError ?? store.bannerMessage {
                BannerView(text: banner, isError: store.bannerError != nil) {
                    store.dismissBanner()
                }
                .padding(.horizontal, 16)
                .padding(.top, 8)
                .transition(.move(edge: .top).combined(with: .opacity))
            }
        }
        .overlay(alignment: .bottomTrailing) {
            Text(appVersion)
                .font(Theme.appFont(size: 10, weight: .medium, design: .rounded))
                .foregroundStyle(Theme.mutedText)
                .padding(.horizontal, 10)
                .padding(.vertical, 6)
                .allowsHitTesting(false)
                .accessibilityLabel("版本 \(appVersion)")
        }
        .animation(.easeInOut(duration: 0.22), value: store.memoryDrawerOpen)
        .animation(.easeInOut(duration: 0.18), value: store.bannerError)
        .animation(.easeInOut(duration: 0.18), value: store.bannerMessage)
        .task(id: store.bannerMessage) {
            guard let message = store.bannerMessage else { return }
            do {
                try await Task.sleep(for: .seconds(5))
            } catch {
                return
            }
            if store.bannerMessage == message {
                store.bannerMessage = nil
            }
        }
        .environment(\.locale, store.interfaceLocale)
        .onReceive(
            NotificationCenter.default.publisher(for: .requestOpenAboutWindow)
        ) { _ in
            openWindow(id: "about")
        }
    }

    private var topbar: some View {
        ZStack {
            brandBlock
        }
        .padding(.horizontal, 18)
        .frame(maxWidth: .infinity)
        .frame(height: Theme.topBarHeight)
        // A translucent material sampled the sidebar and workspace
        // differently, which made the centered brand appear to sit on a
        // large rectangular colour block. Keep this identity strip visually
        // continuous with the window instead.
        .background(Theme.appBackground)
    }

    private var brandBlock: some View {
        HStack(spacing: 12) {
            Image(nsImage: AppBrandIcon.image)
                .resizable()
                .interpolation(.high)
                .frame(width: 34, height: 34)
                .clipShape(
                    RoundedRectangle(cornerRadius: 8, style: .continuous)
                )
            Text("AI Memory")
                .font(Theme.appFont(size: 20, weight: .bold, design: .rounded))
        }
        .accessibilityElement(children: .combine)
    }

    private var appVersion: String {
        let value = Bundle.main.object(
            forInfoDictionaryKey: "CFBundleShortVersionString"
        ) as? String ?? "0.1.1"
        return "v\(value)"
    }
}

/// Routes the workspace pane to the right view based on `store.workspace`.
struct WorkspaceRouter: View {
    @ObservedObject var store: AppStore

    var body: some View {
        Group {
            switch store.workspace {
            case .workbench:
                WorkbenchView(store: store)
            case .conversation:
                ConversationDetailView(store: store)
            case .review:
                ReviewView(store: store)
            case .history:
                HistoryView(store: store)
            case .settings:
                SettingsView(store: store)
            case .favorites:
                FavoritesView(store: store)
            case .trash:
                TrashView(store: store)
            case .help:
                HelpView(store: store)
            }
        }
        .overlay(alignment: .bottomTrailing) {
            if store.workspace != .workbench {
                Button {
                    store.openWorkspace(.workbench)
                } label: {
                    Image(systemName: "arrowshape.backward.fill")
                        .font(Theme.appFont(size: 20, weight: .bold))
                        .symbolRenderingMode(.monochrome)
                        .foregroundStyle(.white)
                        .frame(width: 48, height: 48)
                        .contentShape(Circle())
                }
                .buttonStyle(FloatingWorkbenchBackButtonStyle())
                .background(
                    LinearGradient(
                        colors: [Theme.accent, Theme.accentStrong],
                        startPoint: .topLeading,
                        endPoint: .bottomTrailing
                    ),
                    in: Circle()
                )
                .overlay {
                    Circle()
                        .stroke(.white.opacity(0.42), lineWidth: 1)
                }
                .shadow(color: Theme.accentStrong.opacity(0.30), radius: 13, y: 6)
                .keyboardShortcut("[", modifiers: .command)
                .help("返回工作台 (⌘[)")
                .accessibilityLabel("返回工作台")
                .accessibilityIdentifier("back-to-workbench")
                .padding(.trailing, 18)
                .padding(.bottom, 30)
                .transition(.scale(scale: 0.86).combined(with: .opacity))
            }
        }
        .animation(.easeInOut(duration: 0.18), value: store.workspace)
    }
}

private struct FloatingWorkbenchBackButtonStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .opacity(configuration.isPressed ? 0.82 : 1)
            .scaleEffect(configuration.isPressed ? 0.94 : 1)
            .animation(.easeOut(duration: 0.12), value: configuration.isPressed)
    }
}

// MARK: - Banner

private struct BannerView: View {
    let text: String
    let isError: Bool
    let onDismiss: () -> Void

    var body: some View {
        HStack(spacing: 10) {
            Image(systemName: isError ? "exclamationmark.triangle.fill" : "info.circle.fill")
            Text(text).font(Theme.appFont(size: 12))
            Spacer()
            Button(action: onDismiss) {
                Image(systemName: "xmark.circle.fill")
            }
            .buttonStyle(.borderless)
        }
        .padding(.horizontal, 14).padding(.vertical, 10)
        .foregroundStyle(.white)
        .background(isError ? Theme.danger : Theme.accent)
        .clipShape(RoundedRectangle(cornerRadius: 8))
        .shadow(color: Color.black.opacity(0.15), radius: 8, y: 3)
    }
}

// MARK: - Generic placeholder

struct TextPlaceholderView: View {
    let icon: String
    let title: String
    let message: String

    var body: some View {
        VStack(spacing: 14) {
            Image(systemName: icon)
                .font(Theme.appFont(size: 44, weight: .light))
                .foregroundStyle(Theme.mutedText)
            Text(LocalizedStringKey(title)).font(Theme.appFont(size: 18, weight: .semibold))
            Text(LocalizedStringKey(message))
                .font(Theme.appFont(size: 13))
                .foregroundStyle(Theme.secondaryText)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Theme.appBackground)
    }
}
