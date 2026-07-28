import Foundation
import OSLog

/// Lightweight OSLog wrapper. Subsystem matches the new bundle id so logs
/// surface cleanly in Console.app.
final class Telemetry: @unchecked Sendable {
    private static let subsystem = "com.aimemory.app"

    private let lifecycle = Logger(subsystem: subsystem, category: "Lifecycle")
    private let sidebar = Logger(subsystem: subsystem, category: "Sidebar")
    private let workspace = Logger(subsystem: subsystem, category: "Workspace")
    private let memory = Logger(subsystem: subsystem, category: "Memory")
    private let bridge = Logger(subsystem: subsystem, category: "Bridge")
    private let sync = Logger(subsystem: subsystem, category: "Sync")

    func lifecycle(_ message: String) { lifecycle.notice("\(message, privacy: .public)") }
    func sidebar(_ message: String) { sidebar.notice("\(message, privacy: .public)") }
    func workspace(_ message: String) { workspace.notice("\(message, privacy: .public)") }
    func memory(_ message: String) { memory.notice("\(message, privacy: .public)") }
    func bridge(_ message: String) { bridge.notice("\(message, privacy: .public)") }
    func sync(_ message: String) { sync.notice("\(message, privacy: .public)") }

    func bridgeError(_ message: String) { bridge.error("\(message, privacy: .public)") }
}
