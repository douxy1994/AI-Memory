using System.Text.Json;
using AIMemory.Core.Models;
using AIMemory.Core.Persistence;
using Microsoft.Data.Sqlite;

namespace AIMemory.Core.Services;

public sealed record NativeHistoryImportReport(
    IReadOnlyDictionary<string, int> Imported,
    IReadOnlyList<string> Warnings)
{
    public int Total => Imported.Values.Sum();
}

/// Reads supported local Agent histories without modifying their source files.
/// Parsed conversations are copied into AI Memory's independent database.
public sealed class NativeHistoryImportService
{
    private readonly ConversationRepository _repository;
    private readonly string _home;

    public NativeHistoryImportService(
        ConversationRepository repository,
        string? home = null)
    {
        _repository = repository;
        _home = home
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    public async Task<NativeHistoryImportReport> ImportAllAsync(
        CancellationToken cancellationToken = default)
    {
        var imported = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        imported["codex"] = await ImportSafelyAsync(
            "Codex", ImportCodexAsync, warnings, cancellationToken);
        imported["claude"] = await ImportSafelyAsync(
            "Claude", ImportClaudeAsync, warnings, cancellationToken);
        imported["gemini"] = await ImportSafelyAsync(
            "Gemini", ImportGeminiAsync, warnings, cancellationToken);
        return new NativeHistoryImportReport(imported, warnings);
    }

    private static async Task<int> ImportSafelyAsync(
        string label,
        Func<CancellationToken, Task<int>> operation,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        try
        {
            return await operation(cancellationToken);
        }
        catch (Exception exception)
        {
            warnings.Add($"{label}：{exception.Message}");
            return 0;
        }
    }

    private async Task<int> ImportCodexAsync(CancellationToken cancellationToken)
    {
        var statePath = Path.Combine(_home, ".codex", "state_5.sqlite");
        var indexed = new Dictionary<string, CodexIndexEntry>(
            StringComparer.OrdinalIgnoreCase);
        if (File.Exists(statePath))
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = statePath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
            };
            await using var connection = new SqliteConnection(builder.ToString());
            await connection.OpenAsync(cancellationToken);
            var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id,rollout_path,COALESCE(cwd,''),COALESCE(title,''),
                       created_at,updated_at
                FROM threads
                WHERE source IS NULL OR substr(ltrim(source),1,12)!='{"subagent":'
                ORDER BY updated_at DESC;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var path = reader.IsDBNull(1) ? "" : reader.GetString(1);
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
                var id = reader.GetString(0);
                indexed[path] = new CodexIndexEntry(
                    id,
                    path,
                    reader.GetString(2),
                    reader.GetString(3),
                    EpochToIso(reader.GetValue(4)),
                    EpochToIso(reader.GetValue(5)));
            }
        }

        var sessions = Path.Combine(_home, ".codex", "sessions");
        if (Directory.Exists(sessions))
        {
            foreach (var path in Directory.EnumerateFiles(
                         sessions, "*.jsonl", SearchOption.AllDirectories))
            {
                if (indexed.ContainsKey(path)) continue;
                var timestamp = File.GetLastWriteTimeUtc(path);
                indexed[path] = new CodexIndexEntry(
                    Path.GetFileNameWithoutExtension(path),
                    path,
                    "",
                    "",
                    timestamp.ToString("O"),
                    timestamp.ToString("O"));
            }
        }

        var count = 0;
        foreach (var entry in indexed.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var detail = await ParseCodexAsync(entry, cancellationToken);
                if (detail.Messages.Count == 0) continue;
                await _repository.UpsertAsync(detail, cancellationToken);
                count++;
            }
            catch (JsonException)
            {
                // One malformed rollout must not block importing other sessions.
            }
        }
        return count;
    }

    private async Task<WebDavConversationDetail> ParseCodexAsync(
        CodexIndexEntry entry,
        CancellationToken cancellationToken)
    {
        var messages = new List<WebDavMessage>();
        var cwd = entry.Cwd;
        var first = entry.CreatedAt;
        var last = entry.UpdatedAt;
        await foreach (var line in File.ReadLinesAsync(entry.Path, cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var timestamp = GetString(root, "timestamp") ?? entry.UpdatedAt;
            first = Earlier(first, timestamp);
            last = Later(last, timestamp);
            if (!root.TryGetProperty("payload", out var payload)) continue;
            var outerType = GetString(root, "type") ?? "";
            var payloadType = GetString(payload, "type") ?? "";
            if (outerType == "session_meta")
            {
                cwd = GetString(payload, "cwd") ?? cwd;
                continue;
            }
            var role = payloadType switch
            {
                "user_message" => "user",
                "agent_message" => "assistant",
                _ => null,
            };
            if (role is null) continue;
            var content = GetString(payload, "message") ?? "";
            if (string.IsNullOrWhiteSpace(content)) continue;
            if (role == "user" && !MeaningfulUserText(content)) continue;
            if (messages.LastOrDefault() is { } prior
                && prior.Role == role
                && prior.Content == content)
            {
                continue;
            }
            messages.Add(Message(role, content, timestamp));
        }
        var title = UsefulTitle(entry.Title)
            ?? messages.FirstOrDefault(value => value.Role == "user")?.Content;
        return new WebDavConversationDetail(
            entry.Id,
            "codex",
            string.IsNullOrWhiteSpace(cwd) ? "aimemory://unscoped/codex" : cwd,
            first,
            last,
            Truncate(title, 100),
            entry.Path,
            $"codex resume {entry.Id}",
            messages,
            []);
    }

    private async Task<int> ImportClaudeAsync(CancellationToken cancellationToken)
    {
        var projects = Path.Combine(_home, ".claude", "projects");
        if (!Directory.Exists(projects)) return 0;
        var count = 0;
        foreach (var path in Directory.EnumerateFiles(
                     projects, "*.jsonl", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var detail = await ParseClaudeAsync(path, cancellationToken);
                if (detail.Messages.Count == 0) continue;
                await _repository.UpsertAsync(detail, cancellationToken);
                count++;
            }
            catch (JsonException)
            {
                // Keep importing independent source files.
            }
        }
        return count;
    }

    private static async Task<WebDavConversationDetail> ParseClaudeAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var messages = new List<WebDavMessage>();
        var summary = "";
        var cwd = "";
        var fallback = File.GetLastWriteTimeUtc(path).ToString("O");
        var first = fallback;
        var last = fallback;
        await foreach (var line in File.ReadLinesAsync(path, cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.TryGetProperty("isSidechain", out var sidechain)
                && sidechain.ValueKind == JsonValueKind.True)
            {
                continue;
            }
            cwd = GetString(root, "cwd") ?? cwd;
            if (GetString(root, "type") == "summary")
            {
                summary = GetString(root, "summary") ?? summary;
                continue;
            }
            if (!root.TryGetProperty("message", out var payload)
                || payload.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            var role = GetString(payload, "role");
            if (role is not ("user" or "assistant")) continue;
            var timestamp = GetString(root, "timestamp") ?? fallback;
            first = Earlier(first, timestamp);
            last = Later(last, timestamp);
            if (!payload.TryGetProperty("content", out var content)) continue;
            var text = ExtractText(content);
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (role == "user" && !MeaningfulUserText(text)) continue;
            messages.Add(Message(role, text, timestamp));
        }
        var id = Path.GetFileNameWithoutExtension(path);
        var title = UsefulTitle(summary)
            ?? messages.FirstOrDefault(value => value.Role == "user")?.Content;
        return new WebDavConversationDetail(
            id,
            "claude",
            string.IsNullOrWhiteSpace(cwd)
                ? DecodeClaudeProject(Path.GetFileName(Path.GetDirectoryName(path)) ?? "")
                : cwd,
            first,
            last,
            Truncate(title, 100),
            path,
            $"claude --resume {id}",
            messages,
            []);
    }

    private async Task<int> ImportGeminiAsync(CancellationToken cancellationToken)
    {
        var root = Path.Combine(_home, ".gemini", "tmp");
        if (!Directory.Exists(root)) return 0;
        var count = 0;
        foreach (var path in Directory.EnumerateFiles(
                     root, "*.json", SearchOption.AllDirectories))
        {
            if (!string.Equals(
                    Path.GetFileName(Path.GetDirectoryName(path)),
                    "chats",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = File.OpenRead(path);
                using var document = await JsonDocument.ParseAsync(
                    stream, cancellationToken: cancellationToken);
                var data = document.RootElement;
                var id = GetString(data, "sessionId");
                if (string.IsNullOrWhiteSpace(id)) continue;
                var fallback = File.GetLastWriteTimeUtc(path).ToString("O");
                var created = GetString(data, "startTime") ?? fallback;
                var updated = GetString(data, "lastUpdated") ?? created;
                var project = GetString(data, "projectPath")
                    ?? GetString(data, "cwd")
                    ?? $"gemini:{GetString(data, "projectHash") ?? "unscoped"}";
                var messages = new List<WebDavMessage>();
                if (data.TryGetProperty("messages", out var values)
                    && values.ValueKind == JsonValueKind.Array)
                {
                    foreach (var value in values.EnumerateArray())
                    {
                        var type = GetString(value, "type");
                        var role = type switch
                        {
                            "user" => "user",
                            "gemini" => "assistant",
                            _ => null,
                        };
                        if (role is null) continue;
                        var content = GetString(value, "content") ?? "";
                        if (string.IsNullOrWhiteSpace(content)) continue;
                        if (role == "user" && !MeaningfulUserText(content)) continue;
                        messages.Add(new WebDavMessage(
                            GetString(value, "id") ?? Guid.NewGuid().ToString(),
                            GetString(value, "timestamp") ?? created,
                            role,
                            content,
                            [],
                            []));
                    }
                }
                if (messages.Count == 0) continue;
                var title = GetString(data, "summary")
                    ?? messages.FirstOrDefault(value => value.Role == "user")?.Content;
                await _repository.UpsertAsync(
                    new WebDavConversationDetail(
                        id, "gemini", project, created, updated,
                        Truncate(title, 100), path, $"gemini --resume {id}",
                        messages, []),
                    cancellationToken);
                count++;
            }
            catch (JsonException)
            {
                // Keep importing independent source files.
            }
        }
        return count;
    }

    private static WebDavMessage Message(
        string role,
        string content,
        string timestamp) =>
        new(
            Guid.NewGuid().ToString(),
            timestamp,
            role,
            content,
            [],
            []);

    private static string ExtractText(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? "";
        }
        if (content.ValueKind != JsonValueKind.Array) return "";
        return string.Join(
            "\n\n",
            content.EnumerateArray()
                .Where(value =>
                    value.ValueKind == JsonValueKind.Object
                    && GetString(value, "type") == "text")
                .Select(value => GetString(value, "text"))
                .Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static string? GetString(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(property, out var result)
        && result.ValueKind == JsonValueKind.String
            ? result.GetString()
            : null;

    private static bool MeaningfulUserText(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length > 0
            && !trimmed.StartsWith("<command-name>", StringComparison.Ordinal)
            && !trimmed.StartsWith("<local-command", StringComparison.Ordinal)
            && !trimmed.StartsWith("<system-reminder>", StringComparison.Ordinal);
    }

    private static string? UsefulTitle(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed)
            || trimmed.StartsWith("<", StringComparison.Ordinal)
            ? null
            : trimmed;
    }

    private static string? Truncate(string? value, int length) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Length <= length ? value : value[..length];

    private static string Earlier(string left, string right) =>
        string.CompareOrdinal(left, right) <= 0 ? left : right;

    private static string Later(string left, string right) =>
        string.CompareOrdinal(left, right) >= 0 ? left : right;

    private static string EpochToIso(object value)
    {
        var number = Convert.ToInt64(value);
        if (number > 10_000_000_000) number /= 1_000;
        return DateTimeOffset.FromUnixTimeSeconds(Math.Max(0, number)).ToString("O");
    }

    private static string DecodeClaudeProject(string encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded)) return "aimemory://unscoped/claude";
        // Claude replaces path separators with '-'. The original cwd stored
        // in each event is preferred; this is a stable fallback for old logs.
        return encoded.StartsWith("-", StringComparison.Ordinal)
            ? encoded.Replace('-', Path.DirectorySeparatorChar)
            : encoded;
    }

    private sealed record CodexIndexEntry(
        string Id,
        string Path,
        string Cwd,
        string Title,
        string CreatedAt,
        string UpdatedAt);
}
