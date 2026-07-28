using AIMemory.Core.Models;

namespace AIMemory.Core.Services;

public sealed record AgentDescriptor(
    string Id,
    string Label,
    IReadOnlyList<string> Executables,
    IReadOnlyList<string> RelativePaths,
    bool SupportsAutomaticIntegration);

public sealed class AgentCatalog
{
    private readonly string _home;
    private readonly IReadOnlyList<string> _pathDirectories;

    public AgentCatalog(
        string? home = null,
        IEnumerable<string>? pathDirectories = null)
    {
        _home = home
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _pathDirectories = (pathDirectories
                ?? (Environment.GetEnvironmentVariable("PATH") ?? "")
                    .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            .ToArray();
    }

    public static IReadOnlyList<AgentDescriptor> All { get; } =
    [
        new("claude", "Claude Code", ["claude.exe", "claude.cmd"], [".claude"], true),
        new("codex", "Codex", ["codex.exe", "codex.cmd"], [".codex"], true),
        new("gemini", "Gemini CLI", ["gemini.exe", "gemini.cmd"], [".gemini"], true),
        new("antigravity", "Google Antigravity", ["antigravity.exe"], [".gemini\\antigravity-cli"], true),
        new("opencode", "OpenCode", ["opencode.exe"], [".config\\opencode"], true),
        new("hermes", "Hermes", ["hermes.exe"], [".hermes"], true),
        new("zcode", "ZCode", ["zcode.exe"], [".zcode"], true),
        new("kimi", "Kimi Code", ["kimi.exe", "kimi-code.exe"], [".kimi-code"], true),
        new("cursor", "Cursor", ["cursor.exe", "cursor-agent.exe"], ["AppData\\Local\\Programs\\cursor"], true),
        new("vscode", "Visual Studio Code / Copilot", ["code.exe", "code.cmd"], ["AppData\\Roaming\\Code"], true),
        new("copilot", "GitHub Copilot CLI", ["copilot.exe"], [".copilot"], true),
        new("qwen", "Qwen Code", ["qwen.exe", "qwen.cmd"], [".qwen"], true),
        new("amazonq", "Amazon Q Developer", ["q.exe"], [".aws\\amazonq"], true),
        new("factory", "Factory Droid", ["droid.exe"], [".factory"], true),
        new("windsurf", "Windsurf Cascade", ["windsurf.exe"], [".codeium\\windsurf"], true),
        new("kiro", "Kiro", ["kiro.exe", "kiro-cli.exe"], [".kiro"], true),
        new("continue", "Continue", ["cn.exe", "continue.exe"], [".continue"], false),
        new("goose", "Goose", ["goose.exe"], [".config\\goose"], false),
        new("cline", "Cline", ["cline.exe"], ["AppData\\Roaming\\Code\\User\\globalStorage\\saoudrizwan.claude-dev"], false),
        new("roo", "Roo Code", ["roo.exe"], ["AppData\\Roaming\\Code\\User\\globalStorage\\rooveterinaryinc.roo-cline"], false),
        new("aider", "Aider", ["aider.exe", "aider-chat.exe"], [".aider.conf.yml"], false),
        new("amp", "Amp", ["amp.exe"], [".config\\amp"], false),
        new("warp", "Warp Agent", ["warp.exe"], ["AppData\\Local\\Programs\\Warp"], false),
        new("trae", "Trae", ["trae.exe"], ["AppData\\Local\\Programs\\Trae"], false),
        new("junie", "JetBrains Junie", ["junie.exe"], [".junie"], false),
        new("crush", "Crush", ["crush.exe"], [".config\\crush"], false),
        new("augment", "Augment Code", ["augment.exe"], ["AppData\\Roaming\\Code\\User\\globalStorage\\augment.vscode-augment"], false),
        new("cody", "Sourcegraph Cody", ["cody.exe"], ["AppData\\Roaming\\Code\\User\\globalStorage\\sourcegraph.cody-ai"], false),
        new("tabby", "Tabby", ["tabby.exe"], ["AppData\\Roaming\\Code\\User\\globalStorage\\tabbyml.vscode-tabby"], false),
        new("openhands", "OpenHands", ["openhands.exe"], [".openhands"], false),
        new("open-interpreter", "Open Interpreter", ["interpreter.exe"], [".config\\open-interpreter"], false),
        new("openclaw", "OpenClaw", ["openclaw.exe"], [".openclaw"], false),
        new("codebuddy", "CodeBuddy", ["codebuddy.exe"], [".codebuddy"], false),
        new("devin", "Devin", ["devin.exe"], [".devin"], false),
        new("vibe", "Mistral Vibe", ["vibe", "vibe-acp"], [".vibe"], false),
        new("pi", "Pi Coding Agent", ["pi"], [".pi"], false),
        new("kilo", "Kilo Code CLI", ["kilo"], [".config\\kilo", ".kilo"], false),
        new("plandex", "Plandex", ["plandex", "pdx"], [".plandex"], false),
        new("gptme", "gptme", ["gptme"], [".config\\gptme", ".local\\share\\gptme"], false),
        new("mini-swe-agent", "mini-SWE-agent", ["mini", "mini-extra"], [".config\\mini-swe-agent", "AppData\\Local\\mini-swe-agent"], false),
        new("google-agents-cli", "Google Agents CLI", ["agents-cli"], [".config\\google-agents-cli", "AppData\\Local\\google-agents-cli"], false),
    ];

    public IReadOnlyList<AgentIntegrationStatus> Detect()
    {
        return All.Select((agent, index) =>
            {
                var detected = agent.RelativePaths.Any(relative =>
                        File.Exists(Path.Combine(_home, relative))
                        || Directory.Exists(Path.Combine(_home, relative)))
                    || agent.Executables.Any(executable =>
                        _pathDirectories.Any(directory =>
                            ExecutableExists(directory, executable)));
                return (agent, index, detected);
            })
            .OrderByDescending(value => value.detected)
            .ThenBy(value => value.index)
            .Select(value => new AgentIntegrationStatus(
                value.agent.Id,
                value.agent.Label,
                value.detected,
                value.agent.SupportsAutomaticIntegration,
                false,
                value.detected
                    ? AgentIntegrationState.Detected
                    : AgentIntegrationState.Missing,
                value.detected
                    ? "已检测到本机安装；尚未启用 AI Memory 集成。"
                    : "本机未安装，默认不启用。"))
            .ToArray();
    }

    private static bool ExecutableExists(string directory, string executable)
    {
        if (File.Exists(Path.Combine(directory, executable))) return true;
        if (Path.HasExtension(executable)) return false;
        var extensions = (Environment.GetEnvironmentVariable("PATHEXT")
                ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries);
        return extensions.Any(extension =>
            File.Exists(Path.Combine(directory, executable + extension.ToLowerInvariant()))
            || File.Exists(Path.Combine(directory, executable + extension.ToUpperInvariant())));
    }
}
