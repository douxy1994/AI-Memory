// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
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
        imported["hermes"] = await ImportSafelyAsync(
            "Hermes", ImportHermesAsync, warnings, cancellationToken);
        imported["kimi"] = await ImportSafelyAsync(
            "Kimi Code", ImportKimiAsync, warnings, cancellationToken);
        imported["antigravity"] = await ImportSafelyAsync(
            "Google Antigravity", ImportAntigravityAsync, warnings, cancellationToken);
        imported["opencode"] = await ImportSafelyAsync(
            "OpenCode", ImportOpenCodeAsync, warnings, cancellationToken);
        imported["zcode"] = await ImportSafelyAsync(
            "ZCode", ImportZCodeAsync, warnings, cancellationToken);
        return new NativeHistoryImportReport(imported, warnings);
    }

    public Task<int> ImportAgentAsync(
        string sourceAgent,
        CancellationToken cancellationToken = default) =>
        sourceAgent.Trim().ToLowerInvariant() switch
        {
            "codex" => ImportCodexAsync(cancellationToken),
            "claude" => ImportClaudeAsync(cancellationToken),
            "gemini" => ImportGeminiAsync(cancellationToken),
            "hermes" => ImportHermesAsync(cancellationToken),
            "kimi" => ImportKimiAsync(cancellationToken),
            "antigravity" => ImportAntigravityAsync(cancellationToken),
            "opencode" => ImportOpenCodeAsync(cancellationToken),
            "zcode" => ImportZCodeAsync(cancellationToken),
            _ => Task.FromResult(0),
        };

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
        var fileChanges = new List<WebDavFileChange>();
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
                fileChanges.AddRange(ReadFileChanges(
                    payload, "aimemory_file_changes", timestamp));
                continue;
            }
            if (outerType == "response_item"
                && payloadType == "function_call")
            {
                var callId = GetString(payload, "call_id")
                    ?? Guid.NewGuid().ToString();
                var input = payload.TryGetProperty(
                    "arguments", out var arguments)
                    ? arguments.ValueKind == JsonValueKind.String
                        ? ParseJson(arguments.GetString())
                        : arguments.Clone()
                    : EmptyJson();
                AppendToolCall(
                    messages,
                    new WebDavToolCall(
                        callId,
                        GetString(payload, "name") ?? "tool",
                        input,
                        null,
                        "success"),
                    timestamp);
                continue;
            }
            if (outerType == "response_item"
                && payloadType == "function_call_output")
            {
                var callId = GetString(payload, "call_id");
                if (!string.IsNullOrWhiteSpace(callId))
                {
                    ApplyToolResult(
                        messages,
                        callId,
                        JsonText(payload, "output"),
                        "success");
                }
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
            fileChanges);
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
        var fileChanges = new List<WebDavFileChange>();
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
            if (GetString(root, "type") == "file-history-snapshot")
            {
                fileChanges.AddRange(ReadClaudeFileChanges(root, fallback));
                continue;
            }
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
            if (role == "user")
            {
                foreach (var result in ReadClaudeToolResults(content))
                {
                    ApplyToolResult(
                        messages,
                        result.Id,
                        result.Output,
                        result.Status);
                }
            }
            var tools = role == "assistant"
                ? ReadClaudeToolCalls(content)
                : [];
            if (string.IsNullOrWhiteSpace(text) && tools.Count == 0) continue;
            if (role == "user" && !MeaningfulUserText(text)) continue;
            messages.Add(new WebDavMessage(
                GetString(root, "uuid") ?? Guid.NewGuid().ToString(),
                timestamp,
                role,
                text,
                tools,
                []));
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
            fileChanges);
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
                        var tools = ReadGeminiToolCalls(value);
                        if (string.IsNullOrWhiteSpace(content)
                            && tools.Count == 0)
                        {
                            continue;
                        }
                        if (role == "user" && !MeaningfulUserText(content)) continue;
                        messages.Add(new WebDavMessage(
                            GetString(value, "id") ?? Guid.NewGuid().ToString(),
                            GetString(value, "timestamp") ?? created,
                            role,
                            content,
                            tools,
                            []));
                    }
                }
                if (messages.Count == 0) continue;
                var title = GetString(data, "summary")
                    ?? messages.FirstOrDefault(value => value.Role == "user")?.Content;
                var fileChanges = ReadFileChanges(
                    data, "fileChanges", updated);
                await _repository.UpsertAsync(
                    new WebDavConversationDetail(
                        id, "gemini", project, created, updated,
                        Truncate(title, 100), path, $"gemini --resume {id}",
                        messages, fileChanges),
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

    private async Task<int> ImportHermesAsync(
        CancellationToken cancellationToken)
    {
        var candidates = new[]
        {
            Path.Combine(_home, ".hermes", "state.db"),
            Path.Combine(_home, "AppData", "Roaming", "hermes", "state.db"),
            Path.Combine(_home, "AppData", "Local", "hermes", "state.db"),
        };
        var databasePath = candidates.FirstOrDefault(File.Exists);
        if (databasePath is null) return 0;
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
        };
        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,COALESCE(title,''),started_at,
                   COALESCE(ended_at,started_at),COALESCE(cwd,'')
            FROM sessions WHERE archived=0
            ORDER BY started_at DESC;
            """;
        var rows = new List<(
            string Id,
            string Title,
            object Started,
            object Ended,
            string Cwd)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetValue(2),
                    reader.GetValue(3),
                    reader.GetString(4)));
            }
        }
        var count = 0;
        foreach (var session in rows)
        {
            var messages = connection.CreateCommand();
            messages.CommandText = """
                SELECT id,role,COALESCE(content,''),COALESCE(tool_calls,''),
                       COALESCE(tool_name,''),timestamp
                FROM messages
                WHERE session_id=$session AND active=1
                ORDER BY timestamp;
                """;
            messages.Parameters.AddWithValue("$session", session.Id);
            var parsed = new List<WebDavMessage>();
            await using var reader = await messages.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var role = reader.GetString(1);
                var content = reader.GetString(2);
                var timestamp = EpochToIso(reader.GetValue(5));
                if (role == "tool")
                {
                    if (parsed.LastOrDefault() is { Role: "assistant" } prior)
                    {
                        var toolName = reader.GetString(4);
                        var tools = prior.ToolCalls.ToList();
                        var index = tools.FindLastIndex(value =>
                            value.Output is null && value.Name == toolName);
                        if (index >= 0)
                        {
                            tools[index] = tools[index] with
                            {
                                Output = content,
                                Status = LooksFailed(content) ? "error" : "success",
                            };
                            parsed[^1] = prior with { ToolCalls = tools };
                        }
                    }
                    continue;
                }
                var toolCalls = new List<WebDavToolCall>();
                var rawTools = reader.GetString(3);
                if (!string.IsNullOrWhiteSpace(rawTools))
                {
                    try
                    {
                        using var toolsDocument = JsonDocument.Parse(rawTools);
                        if (toolsDocument.RootElement.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var tool in toolsDocument.RootElement.EnumerateArray())
                            {
                                var function = tool.TryGetProperty(
                                    "function", out var functionValue)
                                    ? functionValue : default;
                                var arguments = function.ValueKind == JsonValueKind.Object
                                    ? GetString(function, "arguments")
                                    : null;
                                toolCalls.Add(new WebDavToolCall(
                                    GetString(tool, "id") ?? Guid.NewGuid().ToString(),
                                    function.ValueKind == JsonValueKind.Object
                                        ? GetString(function, "name") ?? "tool"
                                        : "tool",
                                    ParseJson(arguments),
                                    null,
                                    "success"));
                            }
                        }
                    }
                    catch (JsonException)
                    {
                        // Preserve the visible message even if tool metadata is malformed.
                    }
                }
                if (string.IsNullOrWhiteSpace(content) && toolCalls.Count == 0) continue;
                parsed.Add(new WebDavMessage(
                    reader.GetString(0),
                    timestamp,
                    role is "user" or "assistant" or "system"
                        ? role : "assistant",
                    content,
                    toolCalls,
                    []));
            }
            if (parsed.Count == 0) continue;
            await _repository.UpsertAsync(
                new WebDavConversationDetail(
                    session.Id, "hermes", session.Cwd,
                    EpochToIso(session.Started), EpochToIso(session.Ended),
                    Truncate(UsefulTitle(session.Title), 100),
                    databasePath, $"hermes resume {session.Id}", parsed, []),
                cancellationToken);
            count++;
        }
        return count;
    }

    private async Task<int> ImportKimiAsync(CancellationToken cancellationToken)
    {
        var sessions = Path.Combine(_home, ".kimi-code", "sessions");
        if (!Directory.Exists(sessions)) return 0;
        var count = 0;
        foreach (var session in Directory.EnumerateDirectories(
                     sessions, "*", SearchOption.AllDirectories)
                 .Where(path => Directory.Exists(Path.Combine(path, "agents"))))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var messages = new List<WebDavMessage>();
            var statePath = Path.Combine(session, "state.json");
            var project = "";
            var created = File.GetCreationTimeUtc(session).ToString("O");
            var updated = File.GetLastWriteTimeUtc(session).ToString("O");
            string? title = null;
            if (File.Exists(statePath))
            {
                try
                {
                    using var state = JsonDocument.Parse(
                        await File.ReadAllTextAsync(statePath, cancellationToken));
                    project = GetString(state.RootElement, "workDir") ?? "";
                    created = GetString(state.RootElement, "createdAt") ?? created;
                    updated = GetString(state.RootElement, "updatedAt") ?? updated;
                    title = GetString(state.RootElement, "title");
                }
                catch (JsonException)
                {
                    // The wire logs still contain useful history.
                }
            }
            var agents = Directory.EnumerateDirectories(Path.Combine(session, "agents"))
                .OrderBy(path =>
                    string.Equals(Path.GetFileName(path), "main",
                        StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase);
            var sequence = 0;
            foreach (var agent in agents)
            {
                var wire = Path.Combine(agent, "wire.jsonl");
                if (!File.Exists(wire)) continue;
                await foreach (var line in File.ReadLinesAsync(wire, cancellationToken))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        using var document = JsonDocument.Parse(line);
                        var root = document.RootElement;
                        var timestamp = JsonTimestamp(root, "time") ?? updated;
                        if (GetString(root, "type") == "turn.prompt"
                            && root.TryGetProperty("input", out var input)
                            && input.ValueKind == JsonValueKind.Array)
                        {
                            var text = string.Join(
                                "\n",
                                input.EnumerateArray()
                                    .Where(value => GetString(value, "type") == "text")
                                    .Select(value => GetString(value, "text"))
                                    .Where(value => !string.IsNullOrWhiteSpace(value)));
                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                messages.Add(new WebDavMessage(
                                    $"kimi:{Path.GetFileName(session)}:{sequence++}",
                                    timestamp, "user", text, [], []));
                            }
                            continue;
                        }
                        if (GetString(root, "type") != "context.append_loop_event"
                            || !root.TryGetProperty("event", out var eventValue)
                            || eventValue.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }
                        var eventType = GetString(eventValue, "type");
                        if (eventType == "content.part"
                            && eventValue.TryGetProperty("part", out var part)
                            && part.ValueKind == JsonValueKind.Object)
                        {
                            var text = GetString(part, "text");
                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                messages.Add(new WebDavMessage(
                                    $"kimi:{Path.GetFileName(session)}:{sequence++}",
                                    timestamp, "assistant", text, [], []));
                            }
                        }
                        else if (eventType == "tool.call")
                        {
                            var name = GetString(eventValue, "name") ?? "tool";
                            var callId = GetString(eventValue, "toolCallId")
                                ?? Guid.NewGuid().ToString();
                            var arguments = eventValue.TryGetProperty("args", out var args)
                                ? args.Clone()
                                : EmptyJson();
                            messages.Add(new WebDavMessage(
                                $"kimi:{Path.GetFileName(session)}:{sequence++}",
                                timestamp, "assistant", "",
                                [new WebDavToolCall(
                                    callId, name, arguments, null, "success")],
                                []));
                        }
                    }
                    catch (JsonException)
                    {
                        // Keep reading independent wire events.
                    }
                }
            }
            if (messages.Count == 0) continue;
            var id = Path.GetFileName(session);
            await _repository.UpsertAsync(
                new WebDavConversationDetail(
                    id, "kimi", project, created, updated,
                    Truncate(UsefulTitle(title)
                        ?? messages.FirstOrDefault(value => value.Role == "user")?.Content, 100),
                    File.Exists(statePath) ? statePath : session,
                    $"kimi --session {id}", messages, []),
                cancellationToken);
            count++;
        }
        return count;
    }

    private async Task<int> ImportAntigravityAsync(
        CancellationToken cancellationToken)
    {
        var brain = Path.Combine(_home, ".gemini", "antigravity", "brain");
        if (!Directory.Exists(brain)) return 0;
        var count = 0;
        foreach (var session in Directory.EnumerateDirectories(brain))
        {
            var transcript = Path.Combine(
                session, ".system_generated", "logs", "transcript.jsonl");
            if (!File.Exists(transcript)) continue;
            cancellationToken.ThrowIfCancellationRequested();
            var messages = new List<WebDavMessage>();
            var first = File.GetCreationTimeUtc(transcript).ToString("O");
            var last = File.GetLastWriteTimeUtc(transcript).ToString("O");
            var sequence = 0;
            await foreach (var line in File.ReadLinesAsync(
                               transcript, cancellationToken))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    var source = GetString(root, "source") ?? "";
                    var role = source switch
                    {
                        "USER_EXPLICIT" or "USER" => "user",
                        "MODEL" => "assistant",
                        _ => "system",
                    };
                    var content = GetString(root, "content") ?? "";
                    content = ExtractTag(content, "USER_REQUEST");
                    var timestamp = GetString(root, "created_at") ?? last;
                    first = Earlier(first, timestamp);
                    last = Later(last, timestamp);
                    var tools = new List<WebDavToolCall>();
                    if (root.TryGetProperty("tool_calls", out var toolCalls)
                        && toolCalls.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var tool in toolCalls.EnumerateArray())
                        {
                            tools.Add(new WebDavToolCall(
                                GetString(tool, "id") ?? Guid.NewGuid().ToString(),
                                GetString(tool, "name") ?? "tool",
                                tool.TryGetProperty("args", out var args)
                                    ? args.Clone() : EmptyJson(),
                                null,
                                GetString(root, "status") == "ERROR"
                                    ? "error" : "success"));
                        }
                    }
                    if (string.IsNullOrWhiteSpace(content) && tools.Count == 0) continue;
                    messages.Add(new WebDavMessage(
                        $"antigravity:{Path.GetFileName(session)}:{sequence++}",
                        timestamp, role, content, tools, []));
                }
                catch (JsonException)
                {
                    // Keep importing independent transcript events.
                }
            }
            if (messages.Count == 0) continue;
            var id = Path.GetFileName(session);
            await _repository.UpsertAsync(
                new WebDavConversationDetail(
                    id, "antigravity", session, first, last,
                    Truncate(messages.FirstOrDefault(
                        value => value.Role == "user")?.Content, 100),
                    transcript, null, messages, []),
                cancellationToken);
            count++;
        }
        return count;
    }

    private async Task<int> ImportOpenCodeAsync(
        CancellationToken cancellationToken)
    {
        var candidates = new[]
        {
            Path.Combine(_home, ".local", "share", "opencode", "opencode.db"),
            Path.Combine(_home, ".config", "opencode", "opencode.db"),
            Path.Combine(_home, "AppData", "Local", "opencode", "opencode.db"),
            Path.Combine(_home, "AppData", "Roaming", "opencode", "opencode.db"),
        };
        var databasePath = candidates.FirstOrDefault(File.Exists);
        if (databasePath is null) return 0;
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
        };
        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(cancellationToken);
        var sessions = connection.CreateCommand();
        sessions.CommandText = """
            SELECT id,COALESCE(directory,''),COALESCE(title,''),
                   time_created,time_updated
            FROM session WHERE time_archived IS NULL
            ORDER BY time_updated DESC;
            """;
        var sessionRows = new List<(
            string Id,
            string Project,
            string Title,
            object Created,
            object Updated)>();
        await using (var reader = await sessions.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                sessionRows.Add((
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetValue(3),
                    reader.GetValue(4)));
            }
        }
        var count = 0;
        foreach (var row in sessionRows)
        {
            var id = row.Id;
            var created = EpochToIso(row.Created);
            var updated = EpochToIso(row.Updated);
            var messages = await ReadOpenCodeMessagesAsync(
                connection, id, cancellationToken);
            if (messages.Count == 0) continue;
            var fileChanges = await ReadOpenCodeFileChangesAsync(
                connection, id, cancellationToken);
            await _repository.UpsertAsync(
                new WebDavConversationDetail(
                    id, "opencode", row.Project, created, updated,
                    Truncate(UsefulTitle(row.Title)
                        ?? messages.FirstOrDefault(value => value.Role == "user")?.Content, 100),
                    databasePath, $"opencode --session {id}",
                    messages, fileChanges),
                cancellationToken);
            count++;
        }
        return count;
    }

    private static async Task<IReadOnlyList<WebDavMessage>> ReadOpenCodeMessagesAsync(
        SqliteConnection connection,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,time_created,data FROM message
            WHERE session_id=$session
            ORDER BY time_created,rowid;
            """;
        command.Parameters.AddWithValue("$session", sessionId);
        var rows = new List<(string Id, object Created, string Data)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((
                    reader.GetString(0),
                    reader.GetValue(1),
                    reader.IsDBNull(2) ? "{}" : reader.GetString(2)));
            }
        }
        var result = new List<WebDavMessage>();
        foreach (var row in rows)
        {
            using var messageDocument = JsonDocument.Parse(row.Data);
            var role = GetString(messageDocument.RootElement, "role") switch
            {
                "assistant" => "assistant",
                "system" => "system",
                _ => "user",
            };
            var content = new List<string>();
            var tools = new List<WebDavToolCall>();
            var parts = connection.CreateCommand();
            parts.CommandText = """
                SELECT data FROM part
                WHERE session_id=$session AND message_id=$message
                ORDER BY time_created,rowid;
                """;
            parts.Parameters.AddWithValue("$session", sessionId);
            parts.Parameters.AddWithValue("$message", row.Id);
            await using var partReader = await parts.ExecuteReaderAsync(cancellationToken);
            while (await partReader.ReadAsync(cancellationToken))
            {
                if (partReader.IsDBNull(0)) continue;
                using var partDocument = JsonDocument.Parse(partReader.GetString(0));
                var part = partDocument.RootElement;
                switch (GetString(part, "type"))
                {
                    case "text":
                        var text = GetString(part, "text");
                        if (!string.IsNullOrWhiteSpace(text)) content.Add(text);
                        break;
                    case "file":
                        var file = GetString(part, "filename")
                            ?? GetString(part, "url");
                        if (!string.IsNullOrWhiteSpace(file))
                            content.Add($"[file: {file}]");
                        break;
                    case "tool":
                        var state = part.TryGetProperty("state", out var stateValue)
                            ? stateValue : default;
                        var input = state.ValueKind == JsonValueKind.Object
                            && state.TryGetProperty("input", out var inputValue)
                                ? inputValue.Clone() : EmptyJson();
                        tools.Add(new WebDavToolCall(
                            GetString(part, "callID") ?? Guid.NewGuid().ToString(),
                            GetString(part, "tool") ?? "tool",
                            input,
                            state.ValueKind == JsonValueKind.Object
                                ? GetString(state, "output")
                                    ?? GetString(state, "error")
                                : null,
                            state.ValueKind == JsonValueKind.Object
                                && GetString(state, "status") == "error"
                                    ? "error" : "success"));
                        break;
                }
            }
            if (content.Count == 0 && tools.Count == 0) continue;
            result.Add(new WebDavMessage(
                $"opencode:{sessionId}:{row.Id}",
                EpochToIso(row.Created), role,
                string.Join("\n\n", content), tools, []));
        }
        return result;
    }

    private static async Task<IReadOnlyList<WebDavFileChange>>
        ReadOpenCodeFileChangesAsync(
            SqliteConnection connection,
            string sessionId,
            CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT message_id,time_created,data FROM part
            WHERE session_id=$session
            ORDER BY time_created,rowid;
            """;
        command.Parameters.AddWithValue("$session", sessionId);
        var result = new List<WebDavFileChange>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.IsDBNull(2)) continue;
            using var document = JsonDocument.Parse(reader.GetString(2));
            var part = document.RootElement;
            if (GetString(part, "type") != "patch"
                || !part.TryGetProperty("files", out var files)
                || files.ValueKind != JsonValueKind.Array)
            {
                continue;
            }
            var messageId =
                $"opencode:{sessionId}:{reader.GetString(0)}";
            var timestamp = EpochToIso(reader.GetValue(1));
            foreach (var file in files.EnumerateArray())
            {
                if (file.ValueKind != JsonValueKind.String) continue;
                var path = file.GetString();
                if (string.IsNullOrWhiteSpace(path)) continue;
                result.Add(new WebDavFileChange(
                    path, "modified", timestamp, messageId));
            }
        }
        return result;
    }

    private async Task<int> ImportZCodeAsync(CancellationToken cancellationToken)
    {
        var sessions = Path.Combine(_home, ".zcode", "v2", "sessions");
        if (!Directory.Exists(sessions)) return 0;
        var count = 0;
        foreach (var path in Directory.EnumerateFiles(
                     sessions, "*.json", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var document = JsonDocument.Parse(
                    await File.ReadAllTextAsync(path, cancellationToken));
                var root = document.RootElement;
                var meta = root.TryGetProperty("meta", out var metaValue)
                    ? metaValue : default;
                var profile = Path.GetFileName(Path.GetDirectoryName(path)) ?? "default";
                var provider = meta.ValueKind == JsonValueKind.Object
                    ? (GetString(meta, "provider") ?? "unknown").ToLowerInvariant()
                    : "unknown";
                var task = meta.ValueKind == JsonValueKind.Object
                    ? GetString(meta, "taskId")
                    : null;
                task ??= Path.GetFileNameWithoutExtension(path);
                var id = $"{provider}:task:{profile}:{task}";
                var created = meta.ValueKind == JsonValueKind.Object
                    ? JsonTimestamp(meta, "createdAt") : null;
                created ??= File.GetCreationTimeUtc(path).ToString("O");
                var updated = meta.ValueKind == JsonValueKind.Object
                    ? JsonTimestamp(meta, "updatedAt") : null;
                updated ??= created;
                var project = meta.ValueKind == JsonValueKind.Object
                    ? GetString(meta, "workspacePath")
                        ?? GetString(meta, "cwd")
                        ?? ""
                    : "";
                var messages = new List<WebDavMessage>();
                if (root.TryGetProperty("messages", out var values)
                    && values.ValueKind == JsonValueKind.Array)
                {
                    var index = 0;
                    foreach (var value in values.EnumerateArray())
                    {
                        var role = GetString(value, "role") switch
                        {
                            "assistant" => "assistant",
                            "system" => "system",
                            _ => "user",
                        };
                        var content = GetString(value, "content")
                            ?? ExtractPartsText(value);
                        if (string.IsNullOrWhiteSpace(content)
                            || (role == "user" && !MeaningfulUserText(content)))
                        {
                            continue;
                        }
                        messages.Add(new WebDavMessage(
                            $"zcode:{profile}:{task}:{index++}",
                            JsonTimestamp(value, "timestamp") ?? updated,
                            role, content, [], []));
                    }
                }
                if (messages.Count == 0) continue;
                var title = meta.ValueKind == JsonValueKind.Object
                    ? GetString(meta, "title") : null;
                await _repository.UpsertAsync(
                    new WebDavConversationDetail(
                        id, "zcode", project, created, updated,
                        Truncate(UsefulTitle(title)
                            ?? messages.FirstOrDefault(
                                value => value.Role == "user")?.Content, 100),
                        path, null, messages, []),
                    cancellationToken);
                count++;
            }
            catch (JsonException)
            {
                // Keep importing independent task files.
            }
        }
        return count;
    }

    private static string ExtractTag(string value, string tag)
    {
        var startToken = $"<{tag}>";
        var endToken = $"</{tag}>";
        var start = value.IndexOf(startToken, StringComparison.Ordinal);
        var end = value.IndexOf(endToken, StringComparison.Ordinal);
        return start >= 0 && end > start
            ? value[(start + startToken.Length)..end].Trim()
            : value.Trim();
    }

    private static string ExtractPartsText(JsonElement value)
    {
        if (!value.TryGetProperty("parts", out var parts)
            || parts.ValueKind != JsonValueKind.Array)
        {
            return "";
        }
        return string.Join(
            "\n",
            parts.EnumerateArray().Select(part =>
            {
                var direct = GetString(part, "content");
                if (direct is not null) return direct;
                if (part.TryGetProperty("content", out var nested)
                    && nested.ValueKind == JsonValueKind.Object)
                {
                    return GetString(nested, "text");
                }
                return null;
            }).Where(text => !string.IsNullOrWhiteSpace(text)));
    }

    private static IReadOnlyList<WebDavToolCall> ReadClaudeToolCalls(
        JsonElement content)
    {
        if (content.ValueKind != JsonValueKind.Array) return [];
        var result = new List<WebDavToolCall>();
        foreach (var block in content.EnumerateArray())
        {
            if (GetString(block, "type") != "tool_use") continue;
            result.Add(new WebDavToolCall(
                GetString(block, "id") ?? Guid.NewGuid().ToString(),
                GetString(block, "name") ?? "tool",
                block.TryGetProperty("input", out var input)
                    ? input.Clone()
                    : EmptyJson(),
                null,
                "success"));
        }
        return result;
    }

    private static IReadOnlyList<(
        string Id,
        string? Output,
        string Status)> ReadClaudeToolResults(JsonElement content)
    {
        if (content.ValueKind != JsonValueKind.Array) return [];
        var result = new List<(string, string?, string)>();
        foreach (var block in content.EnumerateArray())
        {
            if (GetString(block, "type") != "tool_result") continue;
            var id = GetString(block, "tool_use_id");
            if (string.IsNullOrWhiteSpace(id)) continue;
            var failed = block.TryGetProperty(
                    "is_error", out var isError)
                && isError.ValueKind == JsonValueKind.True;
            result.Add((
                id,
                JsonText(block, "content"),
                failed ? "error" : "success"));
        }
        return result;
    }

    private static IReadOnlyList<WebDavToolCall> ReadGeminiToolCalls(
        JsonElement message)
    {
        if (!message.TryGetProperty("toolCalls", out var values)
            || values.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        var result = new List<WebDavToolCall>();
        foreach (var value in values.EnumerateArray())
        {
            result.Add(new WebDavToolCall(
                GetString(value, "id") ?? Guid.NewGuid().ToString(),
                GetString(value, "name") ?? "tool",
                value.TryGetProperty("args", out var input)
                    ? input.Clone()
                    : EmptyJson(),
                GetString(value, "resultDisplay")
                    ?? JsonText(value, "result"),
                string.Equals(
                    GetString(value, "status"),
                    "error",
                    StringComparison.OrdinalIgnoreCase)
                    ? "error"
                    : "success"));
        }
        return result;
    }

    private static IReadOnlyList<WebDavFileChange> ReadFileChanges(
        JsonElement parent,
        string property,
        string fallbackTimestamp)
    {
        if (!parent.TryGetProperty(property, out var values)
            || values.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        var result = new List<WebDavFileChange>();
        foreach (var value in values.EnumerateArray())
        {
            var path = GetString(value, "path");
            if (string.IsNullOrWhiteSpace(path)) continue;
            result.Add(new WebDavFileChange(
                path,
                GetString(value, "change_type")
                    ?? GetString(value, "changeType")
                    ?? "modified",
                GetString(value, "timestamp") ?? fallbackTimestamp,
                GetString(value, "message_id")
                    ?? GetString(value, "messageId")));
        }
        return result;
    }

    private static IReadOnlyList<WebDavFileChange> ReadClaudeFileChanges(
        JsonElement root,
        string fallbackTimestamp)
    {
        if (!root.TryGetProperty("snapshot", out var snapshot)
            || snapshot.ValueKind != JsonValueKind.Object
            || !snapshot.TryGetProperty(
                "trackedFileBackups", out var backups)
            || backups.ValueKind != JsonValueKind.Object)
        {
            return [];
        }
        var fallback = GetString(snapshot, "timestamp")
            ?? fallbackTimestamp;
        return backups.EnumerateObject()
            .Select(value => new WebDavFileChange(
                value.Name,
                "modified",
                value.Value.ValueKind == JsonValueKind.Object
                    ? GetString(value.Value, "backupTime") ?? fallback
                    : fallback,
                null))
            .ToArray();
    }

    private static void AppendToolCall(
        List<WebDavMessage> messages,
        WebDavToolCall tool,
        string timestamp)
    {
        var index = messages.FindLastIndex(message =>
            message.Role.Equals(
                "assistant", StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            messages.Add(new WebDavMessage(
                Guid.NewGuid().ToString(),
                timestamp,
                "assistant",
                "",
                [tool],
                []));
            return;
        }
        var message = messages[index];
        messages[index] = message with
        {
            ToolCalls = [.. message.ToolCalls, tool],
        };
    }

    private static void ApplyToolResult(
        List<WebDavMessage> messages,
        string toolId,
        string? output,
        string status)
    {
        for (var messageIndex = messages.Count - 1;
             messageIndex >= 0;
             messageIndex--)
        {
            var message = messages[messageIndex];
            var toolIndex = message.ToolCalls
                .Select((tool, index) => (tool, index))
                .FirstOrDefault(value =>
                    value.tool.Id.Equals(
                        toolId, StringComparison.Ordinal))
                .index;
            if (toolIndex < 0
                || toolIndex >= message.ToolCalls.Count
                || !message.ToolCalls[toolIndex].Id.Equals(
                    toolId, StringComparison.Ordinal))
            {
                continue;
            }
            var tools = message.ToolCalls.ToArray();
            tools[toolIndex] = tools[toolIndex] with
            {
                Output = output,
                Status = status,
            };
            messages[messageIndex] = message with { ToolCalls = tools };
            return;
        }
    }

    private static string? JsonText(
        JsonElement parent,
        string property)
    {
        if (!parent.TryGetProperty(property, out var value)
            || value.ValueKind is JsonValueKind.Null
                or JsonValueKind.Undefined)
        {
            return null;
        }
        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : value.GetRawText();
    }

    private static string? JsonTimestamp(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var timestamp)) return null;
        if (timestamp.ValueKind == JsonValueKind.String)
        {
            var text = timestamp.GetString();
            if (double.TryParse(
                    text,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var number))
            {
                return EpochToIso(number);
            }
            return text;
        }
        return timestamp.ValueKind == JsonValueKind.Number
            ? EpochToIso(timestamp.GetDouble())
            : null;
    }

    private static JsonElement EmptyJson()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    private static JsonElement ParseJson(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return EmptyJson();
        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            using var document = JsonDocument.Parse(
                JsonSerializer.Serialize(new { raw = value }));
            return document.RootElement.Clone();
        }
    }

    private static bool LooksFailed(string? value)
    {
        var text = value?.ToLowerInvariant() ?? "";
        return text.Contains("error", StringComparison.Ordinal)
            || text.Contains("failed", StringComparison.Ordinal)
            || text.Contains("exception", StringComparison.Ordinal);
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
