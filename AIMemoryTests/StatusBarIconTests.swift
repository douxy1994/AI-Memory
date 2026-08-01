// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
import AppKit
import XCTest
@testable import AIMemory

@MainActor
final class StatusBarIconTests: XCTestCase {
    func testTemplateIconRendersAtMenuBarScale() throws {
        let image = StatusBarIcon.make()
        XCTAssertEqual(image.size, NSSize(width: 18, height: 18))
        XCTAssertTrue(image.isTemplate)
        XCTAssertEqual(image.accessibilityDescription, "AI Memory")

        let side = 36
        let bitmap = try XCTUnwrap(
            NSBitmapImageRep(
                bitmapDataPlanes: nil,
                pixelsWide: side,
                pixelsHigh: side,
                bitsPerSample: 8,
                samplesPerPixel: 4,
                hasAlpha: true,
                isPlanar: false,
                colorSpaceName: .deviceRGB,
                bitmapFormat: [],
                bytesPerRow: 0,
                bitsPerPixel: 0
            )
        )
        NSGraphicsContext.saveGraphicsState()
        defer { NSGraphicsContext.restoreGraphicsState() }
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: bitmap)
        image.draw(
            in: NSRect(x: 0, y: 0, width: side, height: side),
            from: .zero,
            operation: .copy,
            fraction: 1
        )

        var visiblePixels = 0
        for y in 0..<side {
            for x in 0..<side {
                let alpha = bitmap.colorAt(x: x, y: y)?.alphaComponent ?? 0
                if alpha > 0.25 {
                    visiblePixels += 1
                }
            }
        }

        XCTAssertGreaterThan(visiblePixels, 180, "The menu-bar head became too faint.")
        XCTAssertLessThan(visiblePixels, 900, "The menu-bar mark became an unreadable solid block.")
    }
}
