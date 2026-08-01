// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
import SwiftUI

/// Design tokens for AI Memory.
///
/// Ported from `native/ChatMemNew/.../SwiftUITheme.swift` (the light-mode
/// sage-green palette of the React/Tauri app) plus dark-mode values lifted
/// from the AppKit `DesignSystem.swift` so the new app supports both
/// appearances. Accent green is identical in both modes.
enum Theme {
    // MARK: - Layout dimensions (from DesignSystem.swift)
    static let sidebarWidth: CGFloat = 330
    static let drawerWidth: CGFloat = 420

    /// User-selectable font family, applied app-wide via `applyFont(_:)`.
    /// Mirrors the four ChatMem options. Persisted in settings.json.
    static nonisolated(unsafe) var fontFamily: FontFamily = .system

    enum FontFamily: String, CaseIterable, Identifiable {
        case system        // 系统默认
        case sourceSans = "source-sans"    // 思源黑体
        case sourceSerif = "source-serif"  // 思源宋体
        case wenkai        // 霞鹜文楷

        var id: String { rawValue }

        var label: String {
            switch self {
            case .system: "系统默认"
            case .sourceSans: "思源黑体"
            case .sourceSerif: "思源宋体"
            case .wenkai: "霞鹜文楷"
            }
        }

        /// Resolve a SwiftUI Font for a given size+weight, using the family's
        /// preferred font when installed; falls back to the system font when
        /// the named font isn't available (SwiftUI's default behavior).
        func font(size: CGFloat, weight: Font.Weight = .regular) -> Font {
            switch self {
            case .system:
                return .system(size: size, weight: weight)
            case .sourceSans:
                return .custom("NotoSansCJKsc", size: size)
            case .sourceSerif:
                return .custom("NotoSerifCJKsc", size: size)
            case .wenkai:
                return .custom("LXGWWenKai", size: size)
            }
        }
    }

    /// Apply a font family globally (called when settings load / change).
    static func applyFont(_ family: FontFamily) {
        fontFamily = family
    }
    /// Compact identity strip: the native titlebar already contributes its
    /// own vertical chrome, so the in-content brand row must stay restrained.
    static let topBarHeight: CGFloat = 52
    static let typeScale: CGFloat = 1.12
    static let cornerRadius: CGFloat = 8
    static let cardCornerRadius: CGFloat = 8
    static let surfaceCornerRadius: CGFloat = 14
    static let compactSpacing: CGFloat = 10
    static let sectionSpacing: CGFloat = 17
    static let controlHeight: CGFloat = 34
    static let recoveryRailWidth: CGFloat = 280
    static let outerPadding: CGFloat = 26
    static let sidebarHPadding: CGFloat = 16

    /// Existing views use explicit point sizes to maintain desktop hierarchy.
    /// Route those sizes through one scale so the complete app remains
    /// comfortably readable without independently retuning every screen.
    static func appFont(
        size: CGFloat,
        weight: Font.Weight = .regular,
        design: Font.Design = .default
    ) -> Font {
        .system(size: size * typeScale, weight: weight, design: design)
    }

    // MARK: - Accent (identical in light/dark)
    static let accent = Color(red: 0.227, green: 0.561, blue: 0.392)        // #3A8F64
    static let accentStrong = Color(red: 0.184, green: 0.455, blue: 0.310)  // #2F744F
    static let danger = Color(red: 0.831, green: 0.322, blue: 0.322)        // #D45252

    // MARK: - Adaptive colors
    static let appBackground = Color("AppBackground")
    static let sidebarBackground = Color("SidebarBackground")
    static let surface = Color("Surface")
    static let soft = Color("Soft")
    static let softStrong = Color("SoftStrong")
    static let selected = Color("SelectedFill")
    static let border = Color("Border")
    static let secondaryText = Color("SecondaryText")
    static let mutedText = Color("MutedText")
    static let primaryText = Color("PrimaryText")
}

extension Color {
    /// Convenience initializer from 0-255 RGB.
    init(_ r8: Double, _ g8: Double, _ b8: Double) {
        self.init(red: r8 / 255, green: g8 / 255, blue: b8 / 255)
    }
}

// MARK: - Card / surface style modifiers

extension View {
    /// Ported from `card(padding:)`. Surface fill + 1pt border +
    /// 8pt corner radius + soft drop shadow.
    func card(padding: CGFloat = 14) -> some View {
        self
            .padding(padding)
            .background(Theme.surface)
            .overlay(
                RoundedRectangle(cornerRadius: Theme.cardCornerRadius)
                    .stroke(Theme.border, lineWidth: 1)
            )
            .clipShape(RoundedRectangle(cornerRadius: Theme.cardCornerRadius))
            .shadow(color: Color.black.opacity(0.035), radius: 12, x: 0, y: 5)
    }

    /// Larger workspace surface card (14pt radius, slightly stronger shadow).
    func surfaceCard(padding: CGFloat = 18) -> some View {
        self
            .padding(padding)
            .background(Theme.surface)
            .overlay(
                RoundedRectangle(cornerRadius: Theme.surfaceCornerRadius)
                    .stroke(Theme.border, lineWidth: 1)
            )
            .clipShape(RoundedRectangle(cornerRadius: Theme.surfaceCornerRadius))
            .shadow(color: Color.black.opacity(0.06), radius: 24, x: 0, y: 12)
    }

    /// Pill / capsule shape used for source selector, search field, status badges.
    func pillShape() -> some View {
        self
            .clipShape(Capsule())
            .overlay(Capsule().stroke(Theme.border, lineWidth: 1))
    }
}
