import Foundation

/// Installs only AI Memory-owned MCP configuration and instruction blocks.
/// Existing ChatMem entries are deliberately left untouched so the two apps
/// can coexist. Every changed pre-existing file receives a timestamped backup.
actor NativeAgentIntegrationStore {
    static var catalogCount: Int { IntegrationAgent.allCases.count }

    private static let blockStart = "<!-- AIMEMORY-INTEGRATION:START -->"
    private static let blockEnd = "<!-- AIMEMORY-INTEGRATION:END -->"
    private static let tomlStart = "# AIMEMORY-INTEGRATION:START"
    private static let tomlEnd = "# AIMEMORY-INTEGRATION:END"

    private let home: URL
    private let helperURL: URL
    private let fileManager = FileManager.default

    init(
        home: URL = FileManager.default.homeDirectoryForCurrentUser,
        helperURL: URL? = nil
    ) {
        self.home = home
        self.helperURL = helperURL
            ?? Bundle.main.bundleURL.appendingPathComponent(
                "Contents/Helpers/aimemory-mcp"
            )
    }

    func detect() -> [AgentIntegrationStatus] {
        IntegrationAgent.allCases
            .map(status)
            .sorted { lhs, rhs in
                if lhs.isAgentDetected != rhs.isAgentDetected {
                    return lhs.isAgentDetected && !rhs.isAgentDetected
                }
                if lhs.mcpInstalled != rhs.mcpInstalled {
                    return lhs.mcpInstalled && !rhs.mcpInstalled
                }
                return IntegrationAgent.catalogIndex(lhs.agent)
                    < IntegrationAgent.catalogIndex(rhs.agent)
            }
    }

    func install(agent key: String) throws -> AgentIntegrationOperation {
        guard let agent = IntegrationAgent(rawValue: key) else {
            throw IntegrationError.unknownAgent(key)
        }
        let detected = status(agent)
        guard detected.isAgentDetected else {
            throw IntegrationError.agentNotInstalled(agent.label)
        }
        guard agent.integrationAvailable else {
            throw IntegrationError.integrationUnavailable(agent.label)
        }
        guard fileManager.isExecutableFile(atPath: helperURL.path) else {
            throw IntegrationError.helperMissing(helperURL.path)
        }
        var backups: [String] = []
        if agent == .codex {
            backups.append(contentsOf: try installCodex(agent: agent))
        } else if agent == .hermes {
            backups.append(contentsOf: try installHermes(agent: agent))
        } else {
            backups.append(contentsOf: try installJSON(agent: agent))
        }
        backups.append(contentsOf: try installInstructions(agent: agent))
        let current = status(agent)
        return operation(
            agent: agent,
            changed: current.mcpInstalled && current.instructionsInstalled,
            message: "\(agent.label) 的 AI Memory 集成已安装或修复。",
            backups: backups,
            status: current
        )
    }

    func uninstall(agent key: String) throws -> AgentIntegrationOperation {
        guard let agent = IntegrationAgent(rawValue: key) else {
            throw IntegrationError.unknownAgent(key)
        }
        var backups: [String] = []
        if agent == .codex {
            if let backup = try removeManagedText(
                at: configPath(agent),
                start: Self.tomlStart,
                end: Self.tomlEnd
            ) { backups.append(backup.path) }
        } else if agent == .hermes {
            if let backup = try removeManagedText(
                at: configPath(agent),
                start: Self.tomlStart,
                end: Self.tomlEnd
            ) { backups.append(backup.path) }
        } else if let backup = try uninstallJSON(agent: agent) {
            backups.append(backup.path)
        }
        backups.append(contentsOf: try uninstallInstructions(agent: agent))
        let current = status(agent)
        return operation(
            agent: agent,
            changed: !backups.isEmpty,
            message: "\(agent.label) 的 AI Memory 集成已卸载；历史和记忆数据未删除。",
            backups: backups,
            status: current
        )
    }

    // MARK: - Status

    private func status(_ agent: IntegrationAgent) -> AgentIntegrationStatus {
        let config = configPath(agent)
        let instructions = instructionPath(agent)
        let mcp = agent.integrationAvailable && mcpInstalled(agent: agent)
        let rules = !agent.supportsInstructions || instructionsInstalled(agent: agent)
        let state: String
        let label: String
        if mcp && rules {
            state = "installed"
            label = "已安装"
        } else if mcp || (agent.supportsInstructions && rules) {
            state = "partial"
            label = "部分安装"
        } else {
            state = "not_installed"
            label = "未安装"
        }
        var details = [
            "使用 AI Memory 自带的原生 Swift MCP helper。",
            "不会覆盖现有 ChatMem 的 chatmem 配置或技能。",
        ]
        if !fileManager.isExecutableFile(atPath: helperURL.path) {
            details.append("当前应用包内未找到可执行 helper，需重新构建应用。")
        }
        let detected = agentDetected(agent)
        if !detected {
            details.append("未检测到应用、CLI 可执行文件或现有配置；默认不启用。")
        } else if !agent.integrationAvailable {
            details.append("已检测到本机安装；该产品暂无可安全自动写入的稳定配置格式。")
        }
        return AgentIntegrationStatus(
            agent: agent.rawValue,
            label: agent.label,
            configPath: config.path,
            instructionsPath: instructions.path,
            mcpInstalled: mcp,
            instructionsInstalled: rules,
            configExists: fileManager.fileExists(atPath: config.path),
            agentDetected: detected,
            integrationAvailable: agent.integrationAvailable,
            status: state,
            statusLabel: label,
            commandPreview: "\"\(helperURL.path)\"",
            details: details
        )
    }

    private func mcpInstalled(agent: IntegrationAgent) -> Bool {
        let path = configPath(agent)
        guard let text = try? String(contentsOf: path, encoding: .utf8) else { return false }
        if agent == .codex || agent == .hermes {
            return text.contains(Self.tomlStart)
                && text.contains(helperURL.path)
        }
        guard let root = try? readJSON(path),
              let parent = root[agent.parentKey] as? [String: Any],
              let server = parent["aimemory"] as? [String: Any] else { return false }
        if let command = server["command"] as? String {
            return command == helperURL.path
        }
        if let command = server["command"] as? [String] {
            return command.first == helperURL.path
        }
        return false
    }

    private func instructionsInstalled(agent: IntegrationAgent) -> Bool {
        if agent == .gemini {
            return (try? String(
                contentsOf: instructionPath(agent),
                encoding: .utf8
            ).contains(Self.blockStart)) ?? false
        }
        let skill = fileManager.fileExists(atPath: instructionPath(agent).path)
        guard agent.needsRules else { return skill }
        let rules = (try? String(
            contentsOf: rulesPath(agent),
            encoding: .utf8
        ).contains(Self.blockStart)) ?? false
        return skill && rules
    }

    // MARK: - Configuration

    private func installJSON(agent: IntegrationAgent) throws -> [String] {
        let path = configPath(agent)
        var root = try readJSON(path)
        var parent = root[agent.parentKey] as? [String: Any] ?? [:]
        parent["aimemory"] = serverConfiguration(agent)
        root[agent.parentKey] = parent
        if agent == .opencode || agent == .zcode {
            if root["$schema"] == nil {
                root["$schema"] = "https://opencode.ai/config.json"
            }
            var tools = root["tools"] as? [String: Any] ?? [:]
            tools["aimemory_*"] = true
            root["tools"] = tools
            var permission = root["permission"] as? [String: Any] ?? [:]
            var skill = permission["skill"] as? [String: Any] ?? [:]
            skill["aimemory"] = "allow"
            permission["skill"] = skill
            root["permission"] = permission
        }
        return try writeJSON(root, to: path).map { [$0.path] } ?? []
    }

    private func uninstallJSON(agent: IntegrationAgent) throws -> URL? {
        let path = configPath(agent)
        guard fileManager.fileExists(atPath: path.path) else { return nil }
        var root = try readJSON(path)
        var changed = false
        if var parent = root[agent.parentKey] as? [String: Any] {
            changed = parent.removeValue(forKey: "aimemory") != nil
            root[agent.parentKey] = parent
        }
        if agent == .opencode || agent == .zcode {
            if var tools = root["tools"] as? [String: Any] {
                changed = tools.removeValue(forKey: "aimemory_*") != nil || changed
                root["tools"] = tools
            }
            if var permission = root["permission"] as? [String: Any],
               var skill = permission["skill"] as? [String: Any] {
                changed = skill.removeValue(forKey: "aimemory") != nil || changed
                permission["skill"] = skill
                root["permission"] = permission
            }
        }
        return changed ? try writeJSON(root, to: path) : nil
    }

    private func serverConfiguration(_ agent: IntegrationAgent) -> [String: Any] {
        switch agent {
        case .vscode:
            [
                "type": "stdio",
                "command": helperURL.path,
                "args": [],
            ]
        case .copilot:
            [
                "type": "stdio",
                "command": helperURL.path,
                "args": [],
                "tools": ["*"],
            ]
        case .factory:
            [
                "type": "stdio",
                "command": helperURL.path,
                "args": [],
                "disabled": false,
            ]
        case .kiro:
            [
                "command": helperURL.path,
                "args": [],
                "env": [:],
                "disabled": false,
                "disabledTools": [],
            ]
        case .windsurf:
            [
                "command": helperURL.path,
                "args": [],
                "env": [:],
            ]
        case .gemini, .antigravity:
            [
                "command": helperURL.path,
                "args": [],
                "timeout": 30_000,
                "trust": true,
            ]
        case .opencode, .zcode:
            [
                "type": "local",
                "command": [helperURL.path],
                "enabled": true,
                "timeout": 30_000,
            ]
        case .kimi:
            [
                "command": helperURL.path,
                "args": [],
                "startupTimeoutMs": 30_000,
            ]
        default:
            [
                "command": helperURL.path,
                "args": [],
                "env": [:],
            ]
        }
    }

    private func installCodex(agent: IntegrationAgent) throws -> [String] {
        let path = configPath(agent)
        let existing = (try? String(contentsOf: path, encoding: .utf8)) ?? ""
        let escaped = helperURL.path
            .replacingOccurrences(of: "\\", with: "\\\\")
            .replacingOccurrences(of: "\"", with: "\\\"")
        let block = """
        \(Self.tomlStart)
        [mcp_servers.aimemory]
        command = "\(escaped)"
        args = []
        startup_timeout_sec = 20
        tool_timeout_sec = 120
        enabled = true
        \(Self.tomlEnd)
        """
        let updated = upsertManagedText(
            existing,
            block: block,
            start: Self.tomlStart,
            end: Self.tomlEnd
        )
        return try writeText(updated, to: path).map { [$0.path] } ?? []
    }

    private func installHermes(agent: IntegrationAgent) throws -> [String] {
        let path = configPath(agent)
        guard fileManager.fileExists(atPath: path.path) else {
            throw IntegrationError.configMissing(path.path)
        }
        let existing = try String(contentsOf: path, encoding: .utf8)
        let block = """
        \(Self.tomlStart)
          aimemory:
            args: []
            command: \(helperURL.path)
            connect_timeout: 30
        \(Self.tomlEnd)
        """
        let updated = upsertManagedText(
            existing,
            block: block,
            start: Self.tomlStart,
            end: Self.tomlEnd
        )
        return try writeText(updated, to: path).map { [$0.path] } ?? []
    }

    // MARK: - Instructions

    private func installInstructions(agent: IntegrationAgent) throws -> [String] {
        var backups: [String] = []
        guard agent.supportsInstructions else { return backups }
        if agent == .gemini {
            let path = instructionPath(agent)
            let existing = (try? String(contentsOf: path, encoding: .utf8)) ?? ""
            let updated = upsertManagedText(
                existing,
                block: managedRules,
                start: Self.blockStart,
                end: Self.blockEnd
            )
            if let backup = try writeText(updated, to: path) {
                backups.append(backup.path)
            }
            return backups
        }
        if let backup = try writeText(skillText, to: instructionPath(agent)) {
            backups.append(backup.path)
        }
        if agent == .codex {
            let yaml = instructionPath(agent).deletingLastPathComponent()
                .appendingPathComponent("agents/openai.yaml")
            if let backup = try writeText(
                "interface:\n  display_name: AI Memory\n  short_description: Native project recall and handoff\n",
                to: yaml
            ) {
                backups.append(backup.path)
            }
        }
        if agent.needsRules {
            let path = rulesPath(agent)
            let existing = (try? String(contentsOf: path, encoding: .utf8)) ?? ""
            let updated = upsertManagedText(
                existing,
                block: managedRules,
                start: Self.blockStart,
                end: Self.blockEnd
            )
            if let backup = try writeText(updated, to: path) {
                backups.append(backup.path)
            }
        }
        return backups
    }

    private func uninstallInstructions(agent: IntegrationAgent) throws -> [String] {
        var backups: [String] = []
        guard agent.supportsInstructions else { return backups }
        if agent == .gemini {
            if let backup = try removeManagedText(
                at: instructionPath(agent),
                start: Self.blockStart,
                end: Self.blockEnd
            ) { backups.append(backup.path) }
            return backups
        }
        let skillDirectory = instructionPath(agent).deletingLastPathComponent()
        if fileManager.fileExists(atPath: skillDirectory.path) {
            try fileManager.removeItem(at: skillDirectory)
        }
        if agent.needsRules,
           let backup = try removeManagedText(
            at: rulesPath(agent),
            start: Self.blockStart,
            end: Self.blockEnd
           ) {
            backups.append(backup.path)
        }
        return backups
    }

    private var managedRules: String {
        """
        \(Self.blockStart)
        ## AI Memory
        Use AI Memory before repository recall, continuation, migration, handoff, or memory questions. Prefer `get_project_context` with `limit=3`, then `search_repo_history` with `limit<=3`. History hits are indexed local evidence, not approved startup rules; identify their source before calling `read_history_conversation`. Use `import_all_local_history` after first install or a suspicious recall miss.
        中文用户问“记得吗、之前聊过、回忆、继续、迁移、交接、项目历史、本地历史、启动规则、记忆”时，先查 AI Memory，再用中文回答。
        \(Self.blockEnd)
        """
    }

    private var skillText: String {
        """
        ---
        name: aimemory
        description: Use AI Memory for repository recall, continuation, migration, handoff, and governed project memory.
        ---

        # AI Memory

        1. Start with `get_project_context` and `limit=3`.
        2. Use `search_repo_history` only for targeted expansion.
        3. Treat local-history hits as evidence, not approved rules.
        4. Ask before expanding a matched conversation with `read_history_conversation`.
        5. Use checkpoints and handoff packets for agent changes.
        """
    }

    // MARK: - Paths

    private func configPath(_ agent: IntegrationAgent) -> URL {
        switch agent {
        case .claude: return home.appendingPathComponent(".claude.json")
        case .codex: return home.appendingPathComponent(".codex/config.toml")
        case .gemini: return home.appendingPathComponent(".gemini/settings.json")
        case .antigravity:
            return home.appendingPathComponent(".gemini/antigravity-cli/mcp_config.json")
        case .opencode:
            return home.appendingPathComponent(".config/opencode/opencode.json")
        case .hermes:
            let app = home.appendingPathComponent(
                "Library/Application Support/hermes/config.yaml"
            )
            let fallback = home.appendingPathComponent(".hermes/config.yaml")
            return fileManager.fileExists(atPath: app.path)
                || !fileManager.fileExists(atPath: fallback.path) ? app : fallback
        case .zcode: return home.appendingPathComponent(".zcode/v2/config.json")
        case .kimi:
            return home.appendingPathComponent(".kimi-code/mcp.json")
        case .cursor:
            return home.appendingPathComponent(".cursor/mcp.json")
        case .vscode:
            return home.appendingPathComponent(
                "Library/Application Support/Code/User/mcp.json"
            )
        case .copilot:
            return home.appendingPathComponent(".copilot/mcp-config.json")
        case .qwen:
            return home.appendingPathComponent(".qwen/settings.json")
        case .amazonq:
            return home.appendingPathComponent(".aws/amazonq/default.json")
        case .factory:
            return home.appendingPathComponent(".factory/mcp.json")
        case .windsurf:
            return home.appendingPathComponent(".codeium/windsurf/mcp_config.json")
        case .kiro:
            return home.appendingPathComponent(".kiro/settings/mcp.json")
        case .continueDev:
            return home.appendingPathComponent(".continue/config.yaml")
        case .goose:
            return home.appendingPathComponent(".config/goose/config.yaml")
        case .cline:
            return home.appendingPathComponent(
                "Library/Application Support/Code/User/globalStorage/saoudrizwan.claude-dev"
            )
        case .roo:
            return home.appendingPathComponent(
                "Library/Application Support/Code/User/globalStorage/rooveterinaryinc.roo-cline"
            )
        case .aider:
            return home.appendingPathComponent(".aider.conf.yml")
        case .amp:
            return home.appendingPathComponent(".config/amp/settings.json")
        case .warp:
            return home.appendingPathComponent(".warp")
        case .trae:
            return home.appendingPathComponent("Library/Application Support/Trae")
        case .junie:
            return home.appendingPathComponent(".junie")
        case .crush:
            return home.appendingPathComponent(".config/crush/crush.json")
        case .augment:
            return home.appendingPathComponent(
                "Library/Application Support/Code/User/globalStorage/augment.vscode-augment"
            )
        case .cody:
            return home.appendingPathComponent(
                "Library/Application Support/Code/User/globalStorage/sourcegraph.cody-ai"
            )
        case .tabby:
            return home.appendingPathComponent(
                "Library/Application Support/Code/User/globalStorage/tabbyml.vscode-tabby"
            )
        case .openhands:
            return home.appendingPathComponent(".openhands")
        case .openInterpreter:
            return home.appendingPathComponent(".config/open-interpreter")
        case .openclaw:
            return home.appendingPathComponent(".openclaw")
        case .codebuddy:
            return home.appendingPathComponent(".codebuddy")
        case .devin:
            return home.appendingPathComponent(".devin")
        case .vibe:
            return home.appendingPathComponent(".vibe/config.toml")
        case .pi:
            return home.appendingPathComponent(".pi/agent/settings.json")
        case .kilo:
            return home.appendingPathComponent(".config/kilo/kilo.json")
        case .plandex:
            return home.appendingPathComponent(".plandex/config.yml")
        case .gptme:
            return home.appendingPathComponent(".config/gptme/config.toml")
        case .miniSweAgent:
            return home.appendingPathComponent(".config/mini-swe-agent/config.yml")
        case .googleAgentsCLI:
            return home.appendingPathComponent(".config/google-agents-cli/config.yml")
        case .rovoDev:
            return home.appendingPathComponent(".rovodev/mcp_config.json")
        case .gitlabDuo:
            return home.appendingPathComponent(".gitlab/storage.json")
        case .grokBuild:
            return home.appendingPathComponent(".grok/config.toml")
        case .jules:
            return home.appendingPathComponent(".aimemory/integrations/jules")
        case .alquimia, .auggie, .firebender, .forge, .ibmBob,
             .iflow, .lingma, .ohMyPi, .qoder, .shai, .sweAgent,
             .tabnineCLI, .zed, .deepagentsCode, .mimoCode, .codebuff,
             .kode, .lettaCode, .nanocoder, .raAid, .conductor, .waza,
             .langsmithCLI, .cortexCode, .clineKanban, .aichat, .llm,
             .fabric, .shellGPT, .elia, .ollama, .lmStudio, .llamaCpp,
             .tgpt, .crewai, .autogpt, .gptscript, .elizaOS, .openAICLI:
            return home.appendingPathComponent(
                ".aimemory/integrations/\(agent.rawValue)"
            )
        }
    }

    private func instructionPath(_ agent: IntegrationAgent) -> URL {
        switch agent {
        case .claude: return home.appendingPathComponent(".claude/skills/aimemory/SKILL.md")
        case .codex: return home.appendingPathComponent(".agents/skills/aimemory/SKILL.md")
        case .gemini: return home.appendingPathComponent(".gemini/GEMINI.md")
        case .antigravity:
            return home.appendingPathComponent(
                ".gemini/antigravity-cli/skills/aimemory/SKILL.md"
            )
        case .opencode:
            return home.appendingPathComponent(
                ".config/opencode/skills/aimemory/SKILL.md"
            )
        case .hermes:
            let app = home.appendingPathComponent(
                "Library/Application Support/hermes/skills/aimemory/SKILL.md"
            )
            let fallback = home.appendingPathComponent(
                ".hermes/skills/aimemory/SKILL.md"
            )
            return fileManager.fileExists(
                atPath: app.deletingLastPathComponent()
                    .deletingLastPathComponent().deletingLastPathComponent().path
            ) || !fileManager.fileExists(
                atPath: fallback.deletingLastPathComponent()
                    .deletingLastPathComponent().deletingLastPathComponent().path
            ) ? app : fallback
        case .zcode: return home.appendingPathComponent(".zcode/skills/aimemory/SKILL.md")
        case .kimi: return home.appendingPathComponent(".kimi-code/skills/aimemory/SKILL.md")
        case .copilot:
            return home.appendingPathComponent(".copilot/skills/aimemory/SKILL.md")
        case .qwen:
            return home.appendingPathComponent(".qwen/skills/aimemory/SKILL.md")
        case .factory:
            return home.appendingPathComponent(".factory/skills/aimemory/SKILL.md")
        case .cursor, .vscode, .amazonq, .windsurf, .kiro,
             .continueDev, .goose, .cline, .roo, .aider, .amp, .warp,
             .trae, .junie, .crush, .augment, .cody, .tabby,
             .openhands, .openInterpreter, .openclaw, .codebuddy, .devin,
             .vibe, .pi, .kilo, .plandex, .gptme, .miniSweAgent,
             .googleAgentsCLI, .rovoDev, .gitlabDuo, .grokBuild, .jules,
             .alquimia, .auggie, .firebender, .forge, .ibmBob,
             .iflow, .lingma, .ohMyPi, .qoder, .shai, .sweAgent,
             .tabnineCLI, .zed, .deepagentsCode, .mimoCode, .codebuff,
             .kode, .lettaCode, .nanocoder, .raAid, .conductor, .waza,
             .langsmithCLI, .cortexCode, .clineKanban, .aichat, .llm,
             .fabric, .shellGPT, .elia, .ollama, .lmStudio, .llamaCpp,
             .tgpt, .crewai, .autogpt, .gptscript, .elizaOS, .openAICLI:
            return home.appendingPathComponent(".aimemory/integrations/\(agent.rawValue)")
        }
    }

    private func rulesPath(_ agent: IntegrationAgent) -> URL {
        switch agent {
        case .claude: return home.appendingPathComponent(".claude/CLAUDE.md")
        case .codex: return home.appendingPathComponent(".codex/AGENTS.md")
        case .antigravity:
            return home.appendingPathComponent(".gemini/antigravity-cli/AGENTS.md")
        case .opencode: return home.appendingPathComponent(".config/opencode/AGENTS.md")
        case .kimi: return home.appendingPathComponent(".kimi-code/AGENTS.md")
        case .copilot: return home.appendingPathComponent(".copilot/copilot-instructions.md")
        case .qwen: return home.appendingPathComponent(".qwen/QWEN.md")
        case .factory: return home.appendingPathComponent(".factory/AGENTS.md")
        default: return instructionPath(agent)
        }
    }

    private func agentDetected(_ agent: IntegrationAgent) -> Bool {
        if agent.detectionPaths(home: home).contains(where: {
            fileManager.fileExists(atPath: $0.path)
        }) {
            return true
        }
        if agent.appNames.contains(where: {
            fileManager.fileExists(atPath: "/Applications/\($0).app")
                || fileManager.fileExists(
                    atPath: home.appendingPathComponent("Applications/\($0).app").path
                )
        }) {
            return true
        }
        if !agent.extensionPrefixes.isEmpty,
           extensionRoots.contains(where: { root in
               guard let entries = try? fileManager.contentsOfDirectory(
                   atPath: root.path
               ) else { return false }
               return entries.contains { entry in
                   agent.extensionPrefixes.contains {
                       entry.lowercased().hasPrefix($0.lowercased())
                   }
               }
           }) {
            return true
        }
        let searchDirectories = (
            (ProcessInfo.processInfo.environment["PATH"] ?? "")
                .split(separator: ":").map(String.init)
            + ["/opt/homebrew/bin", "/usr/local/bin", "/usr/bin"]
        )
        return agent.executables.contains { executable in
            searchDirectories.contains {
                fileManager.isExecutableFile(
                    atPath: URL(fileURLWithPath: $0)
                        .appendingPathComponent(executable).path
                )
            }
        }
    }

    private var extensionRoots: [URL] {
        [
            home.appendingPathComponent(".vscode/extensions"),
            home.appendingPathComponent(".cursor/extensions"),
            home.appendingPathComponent(
                "Library/Application Support/Code/User/globalStorage"
            ),
            home.appendingPathComponent(
                "Library/Application Support/Cursor/User/globalStorage"
            ),
        ]
    }

    // MARK: - Safe file updates

    private func readJSON(_ path: URL) throws -> [String: Any] {
        guard fileManager.fileExists(atPath: path.path) else { return [:] }
        let raw = try String(contentsOf: path, encoding: .utf8)
            .trimmingCharacters(in: CharacterSet(charactersIn: "\u{feff}"))
        let data = Data(removeJSONComments(raw).utf8)
        guard let object = try JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            throw IntegrationError.invalidConfig(path.path)
        }
        return object
    }

    private func writeJSON(_ object: [String: Any], to path: URL) throws -> URL? {
        guard JSONSerialization.isValidJSONObject(object) else {
            throw IntegrationError.invalidConfig(path.path)
        }
        let data = try JSONSerialization.data(
            withJSONObject: object,
            options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
        )
        return try writeText(
            (String(data: data, encoding: .utf8) ?? "{}") + "\n",
            to: path
        )
    }

    private func writeText(_ content: String, to path: URL) throws -> URL? {
        let existing = try? String(contentsOf: path, encoding: .utf8)
        if existing == content { return nil }
        var backup: URL?
        if existing != nil {
            backup = backupPath(path)
            try fileManager.createDirectory(
                at: backup!.deletingLastPathComponent(),
                withIntermediateDirectories: true
            )
            try fileManager.copyItem(at: path, to: backup!)
        }
        try fileManager.createDirectory(
            at: path.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        try Data(content.utf8).write(to: path, options: .atomic)
        return backup
    }

    private func backupPath(_ path: URL) -> URL {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.dateFormat = "yyyyMMdd-HHmmss-SSS"
        return URL(fileURLWithPath: path.path + ".aimemory-backup-" + formatter.string(from: Date()))
    }

    private func upsertManagedText(
        _ existing: String,
        block: String,
        start: String,
        end: String
    ) -> String {
        var text = existing
        if let startRange = text.range(of: start),
           let endRange = text.range(of: end, range: startRange.upperBound..<text.endIndex) {
            text.replaceSubrange(startRange.lowerBound..<endRange.upperBound, with: block)
        } else {
            text = text.trimmingCharacters(in: .whitespacesAndNewlines)
            if !text.isEmpty { text += "\n\n" }
            text += block
        }
        if !text.hasSuffix("\n") { text += "\n" }
        return text
    }

    private func removeManagedText(
        at path: URL,
        start: String,
        end: String
    ) throws -> URL? {
        guard let existing = try? String(contentsOf: path, encoding: .utf8),
              let startRange = existing.range(of: start),
              let endRange = existing.range(
                of: end,
                range: startRange.upperBound..<existing.endIndex
              ) else { return nil }
        var updated = existing
        updated.removeSubrange(startRange.lowerBound..<endRange.upperBound)
        updated = updated.trimmingCharacters(in: .whitespacesAndNewlines)
        if !updated.isEmpty { updated += "\n" }
        return try writeText(updated, to: path)
    }

    private func removeJSONComments(_ input: String) -> String {
        var output = ""
        var index = input.startIndex
        var inString = false
        var escaped = false
        var lineComment = false
        var blockComment = false
        while index < input.endIndex {
            let character = input[index]
            let next = input.index(after: index)
            let following = next < input.endIndex ? input[next] : "\0"
            if lineComment {
                if character == "\n" { lineComment = false; output.append(character) }
            } else if blockComment {
                if character == "*", following == "/" {
                    blockComment = false
                    index = next
                }
            } else if inString {
                output.append(character)
                if escaped {
                    escaped = false
                } else if character == "\\" {
                    escaped = true
                } else if character == "\"" {
                    inString = false
                }
            } else if character == "\"" {
                inString = true
                output.append(character)
            } else if character == "/", following == "/" {
                lineComment = true
                index = next
            } else if character == "/", following == "*" {
                blockComment = true
                index = next
            } else {
                output.append(character)
            }
            index = input.index(after: index)
        }
        return output
    }

    private func operation(
        agent: IntegrationAgent,
        changed: Bool,
        message: String,
        backups: [String],
        status: AgentIntegrationStatus
    ) -> AgentIntegrationOperation {
        AgentIntegrationOperation(
            agent: agent.rawValue,
            label: agent.label,
            changed: changed,
            message: message,
            backupPaths: backups,
            status: status
        )
    }
}

struct AgentIntegrationOperation: Sendable {
    let agent: String
    let label: String
    let changed: Bool
    let message: String
    let backupPaths: [String]
    let status: AgentIntegrationStatus

    func dictionary() throws -> [String: Any] {
        let data = try JSONEncoder().encode(status)
        let statusObject = try JSONSerialization.jsonObject(with: data)
        return [
            "agent": agent,
            "label": label,
            "changed": changed,
            "message": message,
            "backup_paths": backupPaths,
            "status": statusObject,
        ]
    }
}

private enum IntegrationAgent: String, CaseIterable {
    case claude, codex, gemini, antigravity, opencode, hermes, zcode, kimi
    case cursor, vscode, copilot, qwen, amazonq, factory
    case windsurf, kiro
    case continueDev = "continue"
    case goose, cline, roo, aider, amp, warp, trae, junie, crush
    case augment, cody, tabby, openhands
    case openInterpreter = "open-interpreter"
    case openclaw, codebuddy, devin, vibe, pi, kilo, plandex, gptme
    case miniSweAgent = "mini-swe-agent"
    case googleAgentsCLI = "google-agents-cli"
    case rovoDev = "rovo-dev"
    case gitlabDuo = "gitlab-duo"
    case grokBuild = "grok-build"
    case jules
    case alquimia, auggie, firebender, forge
    case ibmBob = "ibm-bob"
    case iflow, lingma
    case ohMyPi = "oh-my-pi"
    case qoder, shai
    case sweAgent = "swe-agent"
    case tabnineCLI = "tabnine-cli"
    case zed
    case deepagentsCode = "deepagents-code"
    case mimoCode = "mimo-code"
    case codebuff, kode
    case lettaCode = "letta-code"
    case nanocoder
    case raAid = "ra-aid"
    case conductor, waza
    case langsmithCLI = "langsmith-cli"
    case cortexCode = "cortex-code"
    case clineKanban = "cline-kanban"
    case aichat, llm, fabric
    case shellGPT = "shell-gpt"
    case elia, ollama
    case lmStudio = "lm-studio"
    case llamaCpp = "llama-cpp"
    case tgpt, crewai, autogpt, gptscript
    case elizaOS = "elizaos"
    case openAICLI = "openai-cli"

    static func catalogIndex(_ rawValue: String) -> Int {
        allCases.firstIndex { $0.rawValue == rawValue } ?? Int.max
    }

    var label: String {
        switch self {
        case .claude: "Claude"
        case .codex: "Codex"
        case .gemini: "Gemini"
        case .antigravity: "Google Antigravity"
        case .opencode: "OpenCode"
        case .hermes: "Hermes"
        case .zcode: "ZCode"
        case .kimi: "Kimi Code"
        case .cursor: "Cursor"
        case .vscode: "Visual Studio Code / Copilot"
        case .copilot: "GitHub Copilot CLI"
        case .qwen: "Qwen Code"
        case .amazonq: "Amazon Q Developer"
        case .factory: "Factory Droid"
        case .windsurf: "Windsurf Cascade"
        case .kiro: "Kiro"
        case .continueDev: "Continue"
        case .goose: "Goose"
        case .cline: "Cline"
        case .roo: "Roo Code"
        case .aider: "Aider"
        case .amp: "Amp"
        case .warp: "Warp Agent"
        case .trae: "Trae"
        case .junie: "JetBrains Junie"
        case .crush: "Crush"
        case .augment: "Augment Code"
        case .cody: "Sourcegraph Cody"
        case .tabby: "Tabby"
        case .openhands: "OpenHands"
        case .openInterpreter: "Open Interpreter"
        case .openclaw: "OpenClaw"
        case .codebuddy: "CodeBuddy"
        case .devin: "Devin"
        case .vibe: "Mistral Vibe"
        case .pi: "Pi Coding Agent"
        case .kilo: "Kilo Code CLI"
        case .plandex: "Plandex"
        case .gptme: "gptme"
        case .miniSweAgent: "mini-SWE-agent"
        case .googleAgentsCLI: "Google Agents CLI"
        case .rovoDev: "Atlassian Rovo Dev"
        case .gitlabDuo: "GitLab Duo CLI"
        case .grokBuild: "xAI Grok Build"
        case .jules: "Google Jules Tools"
        case .alquimia: "Alquimia AI"
        case .auggie: "Auggie CLI"
        case .firebender: "Firebender"
        case .forge: "Forge"
        case .ibmBob: "IBM Bob"
        case .iflow: "iFlow CLI"
        case .lingma: "Lingma"
        case .ohMyPi: "Oh My Pi"
        case .qoder: "Qoder CLI"
        case .shai: "SHAI (OVHcloud)"
        case .sweAgent: "SWE-agent"
        case .tabnineCLI: "Tabnine CLI"
        case .zed: "Zed"
        case .deepagentsCode: "Deep Agents Code"
        case .mimoCode: "MiMo Code"
        case .codebuff: "Codebuff"
        case .kode: "Kode CLI"
        case .lettaCode: "Letta Code"
        case .nanocoder: "Nanocoder"
        case .raAid: "RA.Aid"
        case .conductor: "Microsoft Conductor"
        case .waza: "Microsoft Waza"
        case .langsmithCLI: "LangSmith CLI"
        case .cortexCode: "Snowflake Cortex Code"
        case .clineKanban: "Cline Kanban"
        case .aichat: "AIChat"
        case .llm: "LLM"
        case .fabric: "Fabric"
        case .shellGPT: "ShellGPT"
        case .elia: "Elia"
        case .ollama: "Ollama"
        case .lmStudio: "LM Studio CLI"
        case .llamaCpp: "llama.cpp"
        case .tgpt: "tgpt"
        case .crewai: "CrewAI"
        case .autogpt: "AutoGPT"
        case .gptscript: "GPTScript"
        case .elizaOS: "ElizaOS CLI"
        case .openAICLI: "OpenAI CLI"
        }
    }

    var parentKey: String {
        switch self {
        case .opencode, .zcode: "mcp"
        case .vscode: "servers"
        default: "mcpServers"
        }
    }

    var needsRules: Bool {
        [.claude, .codex, .antigravity, .opencode, .kimi, .copilot, .qwen, .factory]
            .contains(self)
    }

    var supportsInstructions: Bool {
        ![
            .cursor, .vscode, .amazonq, .windsurf, .kiro,
            .continueDev, .goose, .cline, .roo, .aider, .amp, .warp,
            .trae, .junie, .crush, .augment, .cody, .tabby,
            .openhands, .openInterpreter, .openclaw, .codebuddy, .devin,
            .vibe, .pi, .kilo, .plandex, .gptme, .miniSweAgent,
            .googleAgentsCLI, .rovoDev, .gitlabDuo, .grokBuild, .jules,
            .alquimia, .auggie, .firebender, .forge, .ibmBob,
            .iflow, .lingma, .ohMyPi, .qoder, .shai, .sweAgent,
            .tabnineCLI, .zed, .deepagentsCode, .mimoCode, .codebuff,
            .kode, .lettaCode, .nanocoder, .raAid, .conductor, .waza,
            .langsmithCLI, .cortexCode, .clineKanban, .aichat, .llm,
            .fabric, .shellGPT, .elia, .ollama, .lmStudio, .llamaCpp,
            .tgpt, .crewai, .autogpt, .gptscript, .elizaOS, .openAICLI,
        ].contains(self)
    }

    var integrationAvailable: Bool {
        switch self {
        case .continueDev, .goose, .cline, .roo, .aider, .amp, .warp,
             .trae, .junie, .crush, .augment, .cody, .tabby,
             .openhands, .openInterpreter, .openclaw, .codebuddy, .devin,
             .vibe, .pi, .kilo, .plandex, .gptme, .miniSweAgent,
             .googleAgentsCLI, .rovoDev, .gitlabDuo, .grokBuild, .jules,
             .alquimia, .auggie, .firebender, .forge, .ibmBob,
             .iflow, .lingma, .ohMyPi, .qoder, .shai, .sweAgent,
             .tabnineCLI, .zed, .deepagentsCode, .mimoCode, .codebuff,
             .kode, .lettaCode, .nanocoder, .raAid, .conductor, .waza,
             .langsmithCLI, .cortexCode, .clineKanban, .aichat, .llm,
             .fabric, .shellGPT, .elia, .ollama, .lmStudio, .llamaCpp,
             .tgpt, .crewai, .autogpt, .gptscript, .elizaOS, .openAICLI:
            false
        default:
            true
        }
    }

    var executables: [String] {
        switch self {
        case .claude: ["claude"]
        case .codex: ["codex"]
        case .gemini: ["gemini"]
        case .antigravity: ["antigravity", "agy"]
        case .opencode: ["opencode"]
        case .hermes: ["hermes"]
        case .zcode: ["zcode"]
        case .kimi: ["kimi", "kimi-code"]
        case .cursor: ["cursor", "cursor-agent"]
        case .vscode: ["code"]
        case .copilot: ["copilot"]
        case .qwen: ["qwen"]
        case .amazonq: ["q", "qchat"]
        case .factory: ["droid"]
        case .windsurf: ["windsurf"]
        case .kiro: ["kiro", "kiro-cli", "kiro-cli-chat"]
        case .continueDev: ["cn", "continue"]
        case .goose: ["goose"]
        case .cline: ["cline"]
        case .roo: ["roo", "roo-code"]
        case .aider: ["aider", "aider-chat"]
        case .amp: ["amp"]
        case .warp: ["warp"]
        case .trae: ["trae", "traecli", "trae-cli"]
        case .junie: ["junie"]
        case .crush: ["crush"]
        case .augment: ["augment"]
        case .cody: ["cody"]
        case .tabby: ["tabby"]
        case .openhands: ["openhands"]
        case .openInterpreter: ["interpreter"]
        case .openclaw: ["openclaw"]
        case .codebuddy: ["codebuddy", "codebuddy-cli"]
        case .devin: ["devin"]
        case .vibe: ["vibe", "vibe-acp"]
        case .pi: ["pi"]
        case .kilo: ["kilo", "kilocode"]
        case .plandex: ["plandex", "pdx"]
        case .gptme: ["gptme"]
        case .miniSweAgent: ["mini", "mini-extra"]
        case .googleAgentsCLI: ["agents-cli"]
        case .rovoDev: ["acli", "rovodev"]
        case .gitlabDuo: ["duo"]
        case .grokBuild: ["grok"]
        case .jules: ["jules"]
        case .alquimia: ["alquimia"]
        case .auggie: ["auggie"]
        case .firebender: ["firebender"]
        case .forge: ["forge"]
        case .ibmBob: ["bob"]
        case .iflow: ["iflow"]
        case .lingma: ["lingma"]
        case .ohMyPi: ["omp"]
        case .qoder: ["qodercli"]
        case .shai: ["shai"]
        case .sweAgent: ["sweagent"]
        case .tabnineCLI: ["tabnine", "tabnine-cli"]
        case .zed: ["zed"]
        case .deepagentsCode: ["deepagents"]
        case .mimoCode: ["mimo"]
        case .codebuff: ["codebuff", "freebuff"]
        case .kode: ["kode"]
        case .lettaCode: ["letta"]
        case .nanocoder: ["nanocoder"]
        case .raAid: ["ra-aid"]
        case .conductor: ["conductor"]
        case .waza: ["waza"]
        case .langsmithCLI: ["langsmith"]
        case .cortexCode: ["cortex"]
        case .clineKanban: ["kanban"]
        case .aichat: ["aichat"]
        case .llm: ["llm"]
        case .fabric: ["fabric", "fabric-ai"]
        case .shellGPT: ["sgpt"]
        case .elia: ["elia"]
        case .ollama: ["ollama"]
        case .lmStudio: ["lms"]
        case .llamaCpp: ["llama", "llama-cli"]
        case .tgpt: ["tgpt"]
        case .crewai: ["crewai"]
        case .autogpt: ["autogpt"]
        case .gptscript: ["gptscript"]
        case .elizaOS: ["elizaos"]
        case .openAICLI: ["openai"]
        }
    }

    var appNames: [String] {
        switch self {
        case .cursor: ["Cursor"]
        case .vscode: ["Visual Studio Code"]
        case .amazonq: ["Amazon Q"]
        case .factory: ["Droid"]
        case .windsurf: ["Windsurf"]
        case .kiro: ["Kiro"]
        case .goose: ["Goose"]
        case .warp: ["Warp"]
        case .trae: ["Trae"]
        case .junie: ["IntelliJ IDEA", "PyCharm", "WebStorm", "GoLand", "CLion", "Rider"]
        case .openhands: ["OpenHands"]
        case .codebuddy: ["CodeBuddy"]
        case .devin: ["Devin"]
        case .firebender: ["Firebender"]
        case .lingma: ["Lingma"]
        case .qoder: ["Qoder"]
        case .zed: ["Zed"]
        case .ollama: ["Ollama"]
        case .lmStudio: ["LM Studio"]
        default: []
        }
    }

    var extensionPrefixes: [String] {
        switch self {
        case .cline: ["saoudrizwan.claude-dev", "cline.cline"]
        case .roo: ["rooveterinaryinc.roo-cline", "roo-code.roo-code"]
        case .continueDev: ["continue.continue"]
        case .augment: ["augment.vscode-augment"]
        case .cody: ["sourcegraph.cody-ai"]
        case .tabby: ["tabbyml.vscode-tabby"]
        default: []
        }
    }

    func detectionPaths(home: URL) -> [URL] {
        switch self {
        case .hermes:
            [
                home.appendingPathComponent(
                    "Library/Application Support/hermes/config.yaml"
                ),
                home.appendingPathComponent(".hermes/config.yaml"),
            ]
        case .kiro:
            [
                home.appendingPathComponent(".kiro/settings/mcp.json"),
                home.appendingPathComponent(".kiro/mcp.json"),
            ]
        case .amp:
            [
                home.appendingPathComponent(".config/amp/settings.json"),
                home.appendingPathComponent(".amp/settings.json"),
            ]
        case .kilo:
            [
                home.appendingPathComponent(".config/kilo"),
                home.appendingPathComponent(".kilo"),
            ]
        case .gptme:
            [
                home.appendingPathComponent(".config/gptme"),
                home.appendingPathComponent(".local/share/gptme"),
            ]
        case .miniSweAgent:
            [
                home.appendingPathComponent(".config/mini-swe-agent"),
                home.appendingPathComponent(
                    "Library/Application Support/mini-swe-agent"
                ),
            ]
        case .googleAgentsCLI:
            [
                home.appendingPathComponent(".config/google-agents-cli"),
                home.appendingPathComponent(
                    "Library/Application Support/google-agents-cli"
                ),
            ]
        case .rovoDev:
            [home.appendingPathComponent(".rovodev")]
        case .gitlabDuo:
            [home.appendingPathComponent(".gitlab/storage.json")]
        case .grokBuild:
            [home.appendingPathComponent(".grok")]
        case .jules:
            []
        case .sweAgent:
            [home.appendingPathComponent(".config/swe-agent")]
        case .clineKanban:
            []
        case .aichat:
            [
                home.appendingPathComponent(".config/aichat"),
                home.appendingPathComponent("Library/Application Support/aichat"),
            ]
        case .llm:
            [
                home.appendingPathComponent(
                    "Library/Application Support/io.datasette.llm"
                ),
            ]
        case .fabric:
            [home.appendingPathComponent(".config/fabric")]
        case .shellGPT:
            [home.appendingPathComponent(".config/shell_gpt")]
        case .ollama:
            [home.appendingPathComponent(".ollama")]
        case .lmStudio:
            [
                home.appendingPathComponent(
                    "Library/Application Support/LM Studio"
                ),
            ]
        case .tgpt:
            [
                home.appendingPathComponent(
                    "Library/Application Support/tgpt"
                ),
            ]
        case .autogpt:
            [home.appendingPathComponent(".autogpt")]
        case .elia, .llamaCpp, .crewai, .gptscript, .elizaOS, .openAICLI:
            []
        default:
            [defaultDetectionPath(home: home)]
        }
    }

    private func defaultDetectionPath(home: URL) -> URL {
        switch self {
        case .claude: home.appendingPathComponent(".claude.json")
        case .codex: home.appendingPathComponent(".codex/config.toml")
        case .gemini: home.appendingPathComponent(".gemini/settings.json")
        case .antigravity:
            home.appendingPathComponent(".gemini/antigravity-cli/mcp_config.json")
        case .opencode:
            home.appendingPathComponent(".config/opencode/opencode.json")
        case .hermes: home.appendingPathComponent(".hermes")
        case .zcode: home.appendingPathComponent(".zcode")
        case .kimi: home.appendingPathComponent(".kimi-code")
        case .cursor: home.appendingPathComponent(".cursor")
        case .vscode:
            home.appendingPathComponent("Library/Application Support/Code")
        case .copilot: home.appendingPathComponent(".copilot")
        case .qwen: home.appendingPathComponent(".qwen")
        case .amazonq: home.appendingPathComponent(".aws/amazonq")
        case .factory: home.appendingPathComponent(".factory")
        case .windsurf: home.appendingPathComponent(".codeium/windsurf")
        case .kiro: home.appendingPathComponent(".kiro")
        case .continueDev: home.appendingPathComponent(".continue")
        case .goose: home.appendingPathComponent(".config/goose")
        case .cline:
            home.appendingPathComponent(
                "Library/Application Support/Code/User/globalStorage/saoudrizwan.claude-dev"
            )
        case .roo:
            home.appendingPathComponent(
                "Library/Application Support/Code/User/globalStorage/rooveterinaryinc.roo-cline"
            )
        case .aider: home.appendingPathComponent(".aider.conf.yml")
        case .amp: home.appendingPathComponent(".config/amp")
        case .warp: home.appendingPathComponent(".warp")
        case .trae: home.appendingPathComponent("Library/Application Support/Trae")
        case .junie: home.appendingPathComponent(".junie")
        case .crush: home.appendingPathComponent(".config/crush")
        case .augment:
            home.appendingPathComponent(
                "Library/Application Support/Code/User/globalStorage/augment.vscode-augment"
            )
        case .cody:
            home.appendingPathComponent(
                "Library/Application Support/Code/User/globalStorage/sourcegraph.cody-ai"
            )
        case .tabby:
            home.appendingPathComponent(
                "Library/Application Support/Code/User/globalStorage/tabbyml.vscode-tabby"
            )
        case .openhands: home.appendingPathComponent(".openhands")
        case .openInterpreter: home.appendingPathComponent(".config/open-interpreter")
        case .openclaw: home.appendingPathComponent(".openclaw")
        case .codebuddy: home.appendingPathComponent(".codebuddy")
        case .devin: home.appendingPathComponent(".devin")
        case .vibe: home.appendingPathComponent(".vibe")
        case .pi: home.appendingPathComponent(".pi")
        case .kilo: home.appendingPathComponent(".config/kilo")
        case .plandex: home.appendingPathComponent(".plandex")
        case .gptme: home.appendingPathComponent(".config/gptme")
        case .miniSweAgent:
            home.appendingPathComponent(".config/mini-swe-agent")
        case .googleAgentsCLI:
            home.appendingPathComponent(".config/google-agents-cli")
        case .rovoDev: home.appendingPathComponent(".rovodev")
        case .gitlabDuo: home.appendingPathComponent(".gitlab/storage.json")
        case .grokBuild: home.appendingPathComponent(".grok")
        case .jules: home.appendingPathComponent(".config/jules")
        case .alquimia: home.appendingPathComponent(".alquimia")
        case .auggie: home.appendingPathComponent(".augment")
        case .firebender: home.appendingPathComponent(".firebender")
        case .forge: home.appendingPathComponent(".forge")
        case .ibmBob: home.appendingPathComponent(".bob")
        case .iflow: home.appendingPathComponent(".iflow")
        case .lingma: home.appendingPathComponent(".lingma")
        case .ohMyPi: home.appendingPathComponent(".omp")
        case .qoder: home.appendingPathComponent(".qoder")
        case .shai: home.appendingPathComponent(".shai")
        case .sweAgent: home.appendingPathComponent(".config/swe-agent")
        case .tabnineCLI: home.appendingPathComponent(".tabnine")
        case .zed: home.appendingPathComponent(".config/zed")
        case .deepagentsCode: home.appendingPathComponent(".deepagents")
        case .mimoCode: home.appendingPathComponent(".mimocode")
        case .codebuff: home.appendingPathComponent(".codebuff")
        case .kode: home.appendingPathComponent(".kode")
        case .lettaCode: home.appendingPathComponent(".letta")
        case .nanocoder: home.appendingPathComponent(".nanocoder")
        case .raAid: home.appendingPathComponent(".ra-aid")
        case .conductor: home.appendingPathComponent(".conductor")
        case .waza: home.appendingPathComponent(".waza")
        case .langsmithCLI: home.appendingPathComponent(".langsmith")
        case .cortexCode: home.appendingPathComponent(".snowflake/cortex")
        case .clineKanban:
            home.appendingPathComponent(".aimemory/integrations/cline-kanban")
        case .aichat: home.appendingPathComponent(".config/aichat")
        case .llm:
            home.appendingPathComponent(
                "Library/Application Support/io.datasette.llm"
            )
        case .fabric: home.appendingPathComponent(".config/fabric")
        case .shellGPT: home.appendingPathComponent(".config/shell_gpt")
        case .elia: home.appendingPathComponent(".aimemory/integrations/elia")
        case .ollama: home.appendingPathComponent(".ollama")
        case .lmStudio:
            home.appendingPathComponent("Library/Application Support/LM Studio")
        case .llamaCpp:
            home.appendingPathComponent(".aimemory/integrations/llama-cpp")
        case .tgpt:
            home.appendingPathComponent("Library/Application Support/tgpt")
        case .crewai:
            home.appendingPathComponent(".aimemory/integrations/crewai")
        case .autogpt: home.appendingPathComponent(".autogpt")
        case .gptscript:
            home.appendingPathComponent(".aimemory/integrations/gptscript")
        case .elizaOS:
            home.appendingPathComponent(".aimemory/integrations/elizaos")
        case .openAICLI:
            home.appendingPathComponent(".aimemory/integrations/openai-cli")
        }
    }
}

private enum IntegrationError: LocalizedError {
    case unknownAgent(String)
    case helperMissing(String)
    case configMissing(String)
    case invalidConfig(String)
    case agentNotInstalled(String)
    case integrationUnavailable(String)

    var errorDescription: String? {
        switch self {
        case .unknownAgent(let value): "未知代理：\(value)"
        case .helperMissing(let path): "原生 MCP helper 不存在或不可执行：\(path)"
        case .configMissing(let path): "代理配置不存在：\(path)"
        case .invalidConfig(let path): "无法安全解析代理配置：\(path)"
        case .agentNotInstalled(let name): "未检测到 \(name)，因此不会启用或写入配置。"
        case .integrationUnavailable(let name): "\(name) 暂无可安全自动配置的本地集成。"
        }
    }
}
