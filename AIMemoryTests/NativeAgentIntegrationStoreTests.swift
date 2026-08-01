import XCTest
@testable import AIMemory

final class NativeAgentIntegrationStoreTests: XCTestCase {
    func testHelperURLResolutionSupportsAppAndStandaloneBundles() {
        let app = URL(fileURLWithPath: "/Applications/AIMemory.app")
        XCTAssertEqual(
            NativeAgentIntegrationStore.bundledHelperURL(bundleURL: app),
            app.appendingPathComponent("Contents/Helpers/aimemory-mcp")
        )

        let helper = app.appendingPathComponent(
            "Contents/Helpers/aimemory-mcp"
        )
        XCTAssertEqual(
            NativeAgentIntegrationStore.bundledHelperURL(bundleURL: helper),
            helper
        )

        let helpers = app.appendingPathComponent("Contents/Helpers")
        XCTAssertEqual(
            NativeAgentIntegrationStore.bundledHelperURL(bundleURL: helpers),
            helper
        )
    }

    func testJSONIntegrationCoexistsWithChatMemAndUninstallsOnlyOwnEntries() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("NativeAgentIntegrationJSON-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: root) }
        let helper = try makeHelper(in: root)
        let config = root.appendingPathComponent(".claude.json")
        try JSONSerialization.data(
            withJSONObject: [
                "mcpServers": [
                    "chatmem": ["command": "/Applications/ChatMem.app/bridge"]
                ],
                "keep": true,
            ],
            options: [.prettyPrinted]
        ).write(to: config)

        let service = NativeAgentIntegrationStore(home: root, helperURL: helper)
        let result = try await service.install(agent: "claude")
        XCTAssertTrue(result.changed)
        let installed = try json(config)
        let servers = installed["mcpServers"] as? [String: Any]
        XCTAssertNotNil(servers?["chatmem"])
        XCTAssertEqual(
            ((servers?["aimemory"] as? [String: Any])?["command"] as? String),
            helper.path
        )
        let rules = root.appendingPathComponent(".claude/CLAUDE.md")
        XCTAssertTrue(try String(contentsOf: rules).contains("AIMEMORY-INTEGRATION"))

        _ = try await service.uninstall(agent: "claude")
        let uninstalled = try json(config)
        let remaining = uninstalled["mcpServers"] as? [String: Any]
        XCTAssertNotNil(remaining?["chatmem"])
        XCTAssertNil(remaining?["aimemory"])
        XCTAssertFalse(FileManager.default.fileExists(
            atPath: root.appendingPathComponent(".claude/skills/aimemory").path
        ))
    }

    func testCodexManagedBlockPreservesExistingConfigAndCreatesBackup() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("NativeAgentIntegrationCodex-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: root) }
        let helper = try makeHelper(in: root)
        let config = root.appendingPathComponent(".codex/config.toml")
        try FileManager.default.createDirectory(
            at: config.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        let original = """
        model = "gpt-5"

        [mcp_servers.chatmem]
        command = "/Applications/ChatMem.app/bridge"
        """
        try Data(original.utf8).write(to: config)

        let service = NativeAgentIntegrationStore(home: root, helperURL: helper)
        let result = try await service.install(agent: "codex")
        let text = try String(contentsOf: config)
        XCTAssertTrue(text.contains("[mcp_servers.chatmem]"))
        XCTAssertTrue(text.contains("[mcp_servers.aimemory]"))
        XCTAssertTrue(text.contains(helper.path))
        XCTAssertFalse(result.backupPaths.isEmpty)

        _ = try await service.uninstall(agent: "codex")
        let remaining = try String(contentsOf: config)
        XCTAssertTrue(remaining.contains("[mcp_servers.chatmem]"))
        XCTAssertFalse(remaining.contains("[mcp_servers.aimemory]"))
    }

    func testDetectionCoversAllSupportedAgents() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("NativeAgentIntegrationDetect-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: root) }
        let helper = try makeHelper(in: root)
        let service = NativeAgentIntegrationStore(home: root, helperURL: helper)
        let statuses = await service.detect()
        let expectedAgents = [
            "claude", "codex", "gemini", "antigravity",
            "opencode", "hermes", "zcode", "kimi",
            "cursor", "vscode", "copilot", "qwen", "amazonq", "factory",
            "windsurf", "kiro", "continue", "goose", "cline", "roo",
            "aider", "amp", "warp", "trae", "junie", "crush",
            "augment", "cody", "tabby", "openhands", "open-interpreter",
            "openclaw", "codebuddy", "devin", "vibe", "pi", "kilo",
            "plandex", "gptme", "mini-swe-agent", "google-agents-cli",
            "rovo-dev", "gitlab-duo", "grok-build", "jules",
            "alquimia", "auggie", "firebender", "forge", "ibm-bob",
            "iflow", "lingma", "oh-my-pi", "qoder", "shai",
            "swe-agent", "tabnine-cli", "zed",
            "deepagents-code", "mimo-code", "codebuff", "kode",
            "letta-code", "nanocoder", "ra-aid", "conductor", "waza",
            "langsmith-cli", "cortex-code", "cline-kanban",
            "aichat", "llm", "fabric", "shell-gpt", "elia", "ollama",
            "lm-studio", "llama-cpp", "tgpt", "crewai", "autogpt",
            "gptscript", "elizaos", "openai-cli",
            "huggingface-cli", "m365-agents-toolkit",
            "github-agentic-workflows",
            "neovate", "vtcode", "dexto", "nanobot", "zeroclaw",
            "picoclaw", "ironclaw", "nullclaw", "moltis",
            "opensquilla", "qodo", "coderabbit", "poolside",
            "command-code", "ante", "mentat",
            "claw-code", "coro", "nori-cli", "codemachine", "open-codex",
            "groq-code-cli", "devon", "g3", "mini-kode", "zot", "vibepod",
            "every-code", "claw-code-agent", "gitagent", "opendev", "qodex",
            "clawcodex", "tutti", "acpx", "cmux", "muxd", "muxel",
            "flowmux", "mcpjam", "zenflow", "void", "ruflo",
            "claurst", "agentty", "herdr",
            "smol-developer", "claude-engineer", "free-code", "forgecode",
            "autocoderover", "agentless", "codel", "open-harness", "octomind",
            "codex-infinity", "san-agent", "waveloom", "picocode", "qqcode",
            "keen-code", "smelt", "grinta", "zap-agent", "binharic", "darce",
            "claii", "nanoclaw", "clawith", "claw0", "gitclaw", "lionclaw",
            "fetchcoder",
        ]
        XCTAssertEqual(Set(statuses.map(\.agent)), Set(expectedAgents))
        XCTAssertEqual(statuses.count, expectedAgents.count)
        XCTAssertTrue(statuses.allSatisfy { $0.status == "not_installed" })
        XCTAssertTrue(statuses.allSatisfy { !$0.mcpInstalled })
    }

    func testDetectedAgentsAreSortedBeforeMissingAgentsAndUnavailableOnesStayOff() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("NativeAgentIntegrationSort-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: root) }
        let helper = try makeHelper(in: root)
        try FileManager.default.createDirectory(
            at: root.appendingPathComponent(".devin"),
            withIntermediateDirectories: true
        )
        try FileManager.default.createDirectory(
            at: root.appendingPathComponent(
                "Library/Application Support/io.datasette.llm"
            ),
            withIntermediateDirectories: true
        )
        try FileManager.default.createDirectory(
            at: root.appendingPathComponent(".nanobot"),
            withIntermediateDirectories: true
        )
        try FileManager.default.createDirectory(
            at: root.appendingPathComponent(".commandcode"),
            withIntermediateDirectories: true
        )
        let service = NativeAgentIntegrationStore(home: root, helperURL: helper)
        let statuses = await service.detect()

        let firstMissing = statuses.firstIndex { !$0.isAgentDetected }
            ?? statuses.endIndex
        XCTAssertTrue(statuses[..<firstMissing].allSatisfy(\.isAgentDetected))
        XCTAssertTrue(statuses[firstMissing...].allSatisfy { !$0.isAgentDetected })

        let devin = try XCTUnwrap(statuses.first { $0.agent == "devin" })
        XCTAssertTrue(devin.isAgentDetected)
        XCTAssertFalse(devin.canInstallIntegration)
        XCTAssertFalse(devin.mcpInstalled)
        let llm = try XCTUnwrap(statuses.first { $0.agent == "llm" })
        XCTAssertTrue(llm.isAgentDetected)
        XCTAssertFalse(llm.canInstallIntegration)
        XCTAssertFalse(llm.mcpInstalled)
        let nanobot = try XCTUnwrap(
            statuses.first { $0.agent == "nanobot" }
        )
        XCTAssertTrue(nanobot.isAgentDetected)
        XCTAssertEqual(nanobot.status, "detected")
        XCTAssertFalse(nanobot.canInstallIntegration)
        let commandCode = try XCTUnwrap(
            statuses.first { $0.agent == "command-code" }
        )
        XCTAssertTrue(commandCode.isAgentDetected)
        XCTAssertEqual(commandCode.statusLabel, "已检测")
        XCTAssertFalse(commandCode.mcpInstalled)
    }

    func testDetectionRefreshesInjectedPathAndKeepsUnavailableToolsOff() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("NativeAgentIntegrationPath-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: root) }
        let helper = try makeHelper(in: root)
        let bin = root.appendingPathComponent("bin")
        try FileManager.default.createDirectory(
            at: bin,
            withIntermediateDirectories: true
        )
        for executable in ["hf", "atk"] {
            let path = bin.appendingPathComponent(executable)
            try Data().write(to: path)
            try FileManager.default.setAttributes(
                [.posixPermissions: 0o755],
                ofItemAtPath: path.path
            )
        }

        let service = NativeAgentIntegrationStore(
            home: root,
            helperURL: helper,
            pathDirectories: [bin.path]
        )
        let statuses = await service.detect()
        XCTAssertEqual(
            Array(statuses.prefix(2).map(\.agent)),
            ["huggingface-cli", "m365-agents-toolkit"]
        )
        for id in ["huggingface-cli", "m365-agents-toolkit"] {
            let status = try XCTUnwrap(statuses.first { $0.agent == id })
            XCTAssertTrue(status.isAgentDetected)
            XCTAssertEqual(status.status, "detected")
            XCTAssertFalse(status.canInstallIntegration)
            XCTAssertFalse(status.mcpInstalled)
        }
    }

    func testDetectionFindsInstalledDesktopAgentBundle() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("NativeAgentIntegrationAppDetect-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(
            at: root.appendingPathComponent("Applications/Void.app"),
            withIntermediateDirectories: true
        )
        let service = NativeAgentIntegrationStore(
            home: root,
            helperURL: try makeHelper(in: root)
        )

        let statuses = await service.detect()
        XCTAssertEqual(statuses.first?.agent, "void")
        let status = try XCTUnwrap(
            statuses.first { $0.agent == "void" }
        )
        XCTAssertTrue(status.isAgentDetected)
        XCTAssertEqual(status.status, "detected")
        XCTAssertFalse(status.canInstallIntegration)
        XCTAssertFalse(status.mcpInstalled)
    }

    private func makeHelper(in root: URL) throws -> URL {
        try FileManager.default.createDirectory(
            at: root,
            withIntermediateDirectories: true
        )
        let helper = root.appendingPathComponent("aimemory-mcp")
        try Data("#!/bin/sh\nexit 0\n".utf8).write(to: helper)
        try FileManager.default.setAttributes(
            [.posixPermissions: 0o755],
            ofItemAtPath: helper.path
        )
        return helper
    }

    private func json(_ url: URL) throws -> [String: Any] {
        try JSONSerialization.jsonObject(with: Data(contentsOf: url)) as? [String: Any] ?? [:]
    }
}
