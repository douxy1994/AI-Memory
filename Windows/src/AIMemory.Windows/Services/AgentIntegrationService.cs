using System.Text.Json;
using System.Text.Json.Nodes;
using AIMemory.Core.Models;
using AIMemory.Core.Services;

namespace AIMemory.Windows.Services;

public sealed class AgentIntegrationService
{
    private const string BlockStart = "# AIMEMORY-INTEGRATION:START";
    private const string BlockEnd = "# AIMEMORY-INTEGRATION:END";
    private readonly string _home =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private readonly string _helper =
        Path.Combine(AppContext.BaseDirectory, "Helpers", "aimemory-mcp.exe");

    public IReadOnlyList<AgentIntegrationStatus> Detect() =>
        new AgentCatalog().Detect()
            .Select(status =>
            {
                var integrated = IsIntegrated(status.Id);
                return status with
                {
                    IsIntegrated = integrated,
                    State = integrated
                        ? AgentIntegrationState.Integrated
                        : status.State,
                    Detail = integrated
                        ? "AI Memory MCP 已启用。"
                        : status.Detail,
                };
            })
            .OrderByDescending(value => value.IsDetected)
            .ThenByDescending(value => value.IsIntegrated)
            .ThenBy(value => AgentCatalog.All
                .Select((agent, index) => (agent.Id, index))
                .First(pair => pair.Id == value.Id).index)
            .ToArray();

    public void SetEnabled(AgentIntegrationStatus status, bool enabled)
    {
        if (!status.IsDetected)
        {
            throw new InvalidOperationException(
                $"{status.Label} 未安装，不能启用。");
        }
        if (!status.IsIntegrationAvailable)
        {
            throw new InvalidOperationException(
                $"{status.Label} 暂无安全自动配置格式。");
        }
        if (!File.Exists(_helper))
        {
            throw new FileNotFoundException("AI Memory MCP helper 不存在。", _helper);
        }

        if (status.Id is "codex" or "hermes")
        {
            UpdateManagedText(status.Id, enabled);
        }
        else
        {
            UpdateJson(status.Id, enabled);
        }
    }

    private bool IsIntegrated(string id)
    {
        var path = ConfigPath(id);
        if (!File.Exists(path)) return false;
        var text = File.ReadAllText(path);
        return id is "codex" or "hermes"
            ? text.Contains(BlockStart, StringComparison.Ordinal)
              && text.Contains(_helper, StringComparison.OrdinalIgnoreCase)
            : text.Contains("\"aimemory\"", StringComparison.Ordinal)
              && text.Contains("aimemory-mcp.exe", StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateJson(string id, bool enabled)
    {
        var path = ConfigPath(id);
        JsonObject root;
        if (File.Exists(path))
        {
            Backup(path);
            root = JsonNode.Parse(
                File.ReadAllText(path),
                new JsonNodeOptions { PropertyNameCaseInsensitive = false },
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                }) as JsonObject
                ?? throw new InvalidDataException($"无法解析 {path}。");
        }
        else
        {
            root = new JsonObject();
        }
        var parentKey = id switch
        {
            "opencode" or "zcode" => "mcp",
            "vscode" => "servers",
            _ => "mcpServers",
        };
        var parent = root[parentKey] as JsonObject ?? new JsonObject();
        if (enabled)
        {
            parent["aimemory"] = ServerConfiguration(id);
        }
        else
        {
            parent.Remove("aimemory");
        }
        root[parentKey] = parent;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(
            path,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    private JsonObject ServerConfiguration(string id) =>
        id switch
        {
            "opencode" or "zcode" => new JsonObject
            {
                ["type"] = "local",
                ["command"] = new JsonArray(JsonValue.Create(_helper)),
                ["enabled"] = true,
                ["timeout"] = 30_000,
            },
            "vscode" or "copilot" => new JsonObject
            {
                ["type"] = "stdio",
                ["command"] = _helper,
                ["args"] = new JsonArray(),
            },
            "factory" or "kiro" => new JsonObject
            {
                ["command"] = _helper,
                ["args"] = new JsonArray(),
                ["disabled"] = false,
            },
            _ => new JsonObject
            {
                ["command"] = _helper,
                ["args"] = new JsonArray(),
                ["env"] = new JsonObject(),
            },
        };

    private void UpdateManagedText(string id, bool enabled)
    {
        var path = ConfigPath(id);
        var text = File.Exists(path) ? File.ReadAllText(path) : "";
        if (File.Exists(path)) Backup(path);
        var block = id == "codex"
            ? $"""
              {BlockStart}
              [mcp_servers.aimemory]
              command = "{_helper.Replace("\\", "\\\\")}"
              args = []
              enabled = true
              {BlockEnd}
              """
            : $"""
              {BlockStart}
                aimemory:
                  command: {_helper}
                  args: []
                  connect_timeout: 30
              {BlockEnd}
              """;
        var updated = ReplaceBlock(text, enabled ? block : null);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, updated);
    }

    private static string ReplaceBlock(string text, string? replacement)
    {
        var start = text.IndexOf(BlockStart, StringComparison.Ordinal);
        var end = text.IndexOf(BlockEnd, StringComparison.Ordinal);
        if (start >= 0 && end >= start)
        {
            text = text.Remove(start, end + BlockEnd.Length - start).Trim();
        }
        if (replacement is not null)
        {
            if (text.Length > 0) text += Environment.NewLine + Environment.NewLine;
            text += replacement.Trim();
        }
        return text.TrimEnd() + Environment.NewLine;
    }

    private string ConfigPath(string id) =>
        id switch
        {
            "claude" => Path.Combine(_home, ".claude.json"),
            "codex" => Path.Combine(_home, ".codex", "config.toml"),
            "gemini" => Path.Combine(_home, ".gemini", "settings.json"),
            "antigravity" => Path.Combine(_home, ".gemini", "antigravity-cli", "mcp_config.json"),
            "opencode" => Path.Combine(_home, ".config", "opencode", "opencode.json"),
            "hermes" => Path.Combine(_home, ".hermes", "config.yaml"),
            "zcode" => Path.Combine(_home, ".zcode", "v2", "config.json"),
            "kimi" => Path.Combine(_home, ".kimi-code", "mcp.json"),
            "cursor" => Path.Combine(_home, ".cursor", "mcp.json"),
            "vscode" => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Code", "User", "mcp.json"),
            "copilot" => Path.Combine(_home, ".copilot", "mcp-config.json"),
            "qwen" => Path.Combine(_home, ".qwen", "settings.json"),
            "amazonq" => Path.Combine(_home, ".aws", "amazonq", "default.json"),
            "factory" => Path.Combine(_home, ".factory", "mcp.json"),
            "windsurf" => Path.Combine(_home, ".codeium", "windsurf", "mcp_config.json"),
            "kiro" => Path.Combine(_home, ".kiro", "settings", "mcp.json"),
            _ => throw new InvalidOperationException($"不支持自动配置：{id}"),
        };

    private static void Backup(string path)
    {
        var backup = $"{path}.aimemory-backup-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}";
        File.Copy(path, backup, false);
    }
}
