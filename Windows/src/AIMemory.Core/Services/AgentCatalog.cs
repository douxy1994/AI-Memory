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
        new("claude", "Claude Code", ["claude"], [".claude"], true),
        new("codex", "Codex", ["codex"], [".codex"], true),
        new("gemini", "Gemini CLI", ["gemini"], [".gemini"], true),
        new("antigravity", "Google Antigravity", ["antigravity", "agy"], [".gemini\\antigravity-cli"], true),
        new("opencode", "OpenCode", ["opencode"], [".config\\opencode"], true),
        new("hermes", "Hermes", ["hermes"], [".hermes"], true),
        new("zcode", "ZCode", ["zcode"], [".zcode"], true),
        new("kimi", "Kimi Code", ["kimi", "kimi-code"], [".kimi-code"], true),
        new("cursor", "Cursor", ["cursor", "cursor-agent"], ["AppData\\Local\\Programs\\cursor"], true),
        new("vscode", "Visual Studio Code / Copilot", ["code"], ["AppData\\Roaming\\Code"], true),
        new("copilot", "GitHub Copilot CLI", ["copilot"], [".copilot"], true),
        new("qwen", "Qwen Code", ["qwen"], [".qwen"], true),
        new("amazonq", "Amazon Q Developer", ["q", "qchat"], [".aws\\amazonq"], true),
        new("factory", "Factory Droid", ["droid"], [".factory"], true),
        new("windsurf", "Windsurf Cascade", ["windsurf"], [".codeium\\windsurf"], true),
        new("kiro", "Kiro", ["kiro", "kiro-cli", "kiro-cli-chat"], [".kiro"], true),
        new("continue", "Continue", ["cn", "continue"], [".continue"], false),
        new("goose", "Goose", ["goose"], [".config\\goose"], false),
        new("cline", "Cline", ["cline"], ["AppData\\Roaming\\Code\\User\\globalStorage\\saoudrizwan.claude-dev"], false),
        new("roo", "Roo Code", ["roo", "roo-code"], ["AppData\\Roaming\\Code\\User\\globalStorage\\rooveterinaryinc.roo-cline"], false),
        new("aider", "Aider", ["aider", "aider-chat"], [".aider.conf.yml"], false),
        new("amp", "Amp", ["amp"], [".config\\amp"], false),
        new("warp", "Warp Agent", ["warp"], ["AppData\\Local\\Programs\\Warp"], false),
        new("trae", "Trae", ["trae", "traecli", "trae-cli"], ["AppData\\Local\\Programs\\Trae"], false),
        new("junie", "JetBrains Junie", ["junie"], [".junie"], false),
        new("crush", "Crush", ["crush"], [".config\\crush"], false),
        new("augment", "Augment Code", ["augment"], ["AppData\\Roaming\\Code\\User\\globalStorage\\augment.vscode-augment"], false),
        new("cody", "Sourcegraph Cody", ["cody"], ["AppData\\Roaming\\Code\\User\\globalStorage\\sourcegraph.cody-ai"], false),
        new("tabby", "Tabby", ["tabby"], ["AppData\\Roaming\\Code\\User\\globalStorage\\tabbyml.vscode-tabby"], false),
        new("openhands", "OpenHands", ["openhands"], [".openhands"], false),
        new("open-interpreter", "Open Interpreter", ["interpreter"], [".config\\open-interpreter"], false),
        new("openclaw", "OpenClaw", ["openclaw"], [".openclaw"], false),
        new("codebuddy", "CodeBuddy", ["codebuddy", "codebuddy-cli"], [".codebuddy"], false),
        new("devin", "Devin", ["devin"], [".devin"], false),
        new("vibe", "Mistral Vibe", ["vibe", "vibe-acp"], [".vibe"], false),
        new("pi", "Pi Coding Agent", ["pi"], [".pi"], false),
        new("kilo", "Kilo Code CLI", ["kilo", "kilocode"], [".config\\kilo", ".kilo"], false),
        new("plandex", "Plandex", ["plandex", "pdx"], [".plandex"], false),
        new("gptme", "gptme", ["gptme"], [".config\\gptme", ".local\\share\\gptme"], false),
        new("mini-swe-agent", "mini-SWE-agent", ["mini", "mini-extra"], [".config\\mini-swe-agent", "AppData\\Local\\mini-swe-agent"], false),
        new("google-agents-cli", "Google Agents CLI", ["agents-cli"], [".config\\google-agents-cli", "AppData\\Local\\google-agents-cli"], false),
        new("rovo-dev", "Atlassian Rovo Dev", ["acli", "rovodev"], [".rovodev"], false),
        new("gitlab-duo", "GitLab Duo CLI", ["duo"], [".gitlab\\storage.json"], false),
        new("grok-build", "xAI Grok Build", ["grok"], [".grok"], false),
        new("jules", "Google Jules Tools", ["jules"], [], false),
        new("alquimia", "Alquimia AI", ["alquimia"], [".alquimia"], false),
        new("auggie", "Auggie CLI", ["auggie"], [".augment"], false),
        new("firebender", "Firebender", ["firebender"], [".firebender"], false),
        new("forge", "Forge", ["forge"], [".forge"], false),
        new("ibm-bob", "IBM Bob", ["bob"], [".bob"], false),
        new("iflow", "iFlow CLI", ["iflow"], [".iflow"], false),
        new("lingma", "Lingma", ["lingma"], [".lingma"], false),
        new("oh-my-pi", "Oh My Pi", ["omp"], [".omp"], false),
        new("qoder", "Qoder CLI", ["qodercli"], [".qoder"], false),
        new("shai", "SHAI (OVHcloud)", ["shai"], [".shai"], false),
        new("swe-agent", "SWE-agent", ["sweagent"], [".config\\swe-agent"], false),
        new("tabnine-cli", "Tabnine CLI", ["tabnine", "tabnine-cli"], [".tabnine"], false),
        new("zed", "Zed", ["zed"], [".config\\zed", "AppData\\Local\\Programs\\Zed"], false),
        new("deepagents-code", "Deep Agents Code", ["deepagents"], [".deepagents"], false),
        new("mimo-code", "MiMo Code", ["mimo"], [".mimocode"], false),
        new("codebuff", "Codebuff", ["codebuff", "freebuff"], [".codebuff"], false),
        new("kode", "Kode CLI", ["kode"], [".kode", ".kode.json"], false),
        new("letta-code", "Letta Code", ["letta"], [".letta"], false),
        new("nanocoder", "Nanocoder", ["nanocoder"], [".nanocoder"], false),
        new("ra-aid", "RA.Aid", ["ra-aid"], [".ra-aid"], false),
        new("conductor", "Microsoft Conductor", ["conductor"], [".conductor"], false),
        new("waza", "Microsoft Waza", ["waza"], [".waza"], false),
        new("langsmith-cli", "LangSmith CLI", ["langsmith"], [".langsmith"], false),
        new("cortex-code", "Snowflake Cortex Code", ["cortex"], [".snowflake\\cortex"], false),
        new("cline-kanban", "Cline Kanban", ["kanban"], [], false),
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
