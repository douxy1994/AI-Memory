using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIMemory.Core.Models;
using AIMemory.Core.Persistence;
using Microsoft.Data.Sqlite;

namespace AIMemory.Core.Services;

public sealed record NativeAgentWriteResult(
    string Id,
    string StoragePath,
    string? ResumeCommand);

public sealed record NativeSourceArchive(
    string Agent,
    string ConversationId,
    string Kind,
    string OriginalPath,
    string BackupPath,
    string? DatabasePath,
    IReadOnlyDictionary<string, string?> Metadata);

/// Writes only the four native history formats that AI Memory can read back
/// and verify. Other agents remain detection/search targets until their local
/// stores have a stable, tested write contract.
public sealed class NativeAgentConversationWriter
{
    public static IReadOnlySet<string> WritableTargets { get; } =
        new HashSet<string>(
            ["claude", "codex", "gemini", "opencode"],
            StringComparer.OrdinalIgnoreCase);
    public static IReadOnlySet<string> ArchivableSources { get; } =
        new HashSet<string>(
            [
                "claude", "codex", "gemini", "kimi",
                "antigravity", "opencode", "zcode",
            ],
            StringComparer.OrdinalIgnoreCase);

    private readonly string _home;
    private readonly string _archiveRoot;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public NativeAgentConversationWriter(
        string? home = null,
        string? archiveRoot = null)
    {
        _home = home
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _archiveRoot = archiveRoot
            ?? Path.Combine(DataPaths.TrashDirectory, "raw");
    }

    public async Task<NativeSourceArchive> ArchiveSourceAsync(
        WebDavConversationDetail conversation,
        CancellationToken cancellationToken = default)
    {
        return conversation.SourceAgent.ToLowerInvariant() switch
        {
            "codex" => await ArchiveCodexAsync(
                conversation.Id, cancellationToken),
            "opencode" => await ArchiveOpenCodeAsync(
                conversation.Id, cancellationToken),
            "hermes" => throw new NotSupportedException(
                "Hermes 不支持安全归档原始会话。"),
            _ => ArchiveFileBackedSource(conversation),
        };
    }

    public async Task RestoreSourceArchiveAsync(
        NativeSourceArchive archive,
        CancellationToken cancellationToken = default)
    {
        switch (archive.Kind)
        {
            case "file":
                RestoreMovedPath(archive, directory: false);
                return;
            case "directory":
                RestoreMovedPath(archive, directory: true);
                return;
            case "opencode":
                await SetOpenCodeArchivedAsync(
                    archive.DatabasePath
                        ?? throw new InvalidDataException(
                            "OpenCode 归档缺少数据库路径。"),
                    archive.ConversationId,
                    archived: false,
                    cancellationToken);
                return;
            case "codex":
                await RestoreCodexArchiveAsync(archive, cancellationToken);
                return;
            default:
                throw new InvalidDataException(
                    $"未知原始会话归档类型：{archive.Kind}");
        }
    }

    public async Task DeleteSourceArchiveAsync(
        NativeSourceArchive archive,
        CancellationToken cancellationToken = default)
    {
        if (archive.Kind == "opencode")
        {
            await DeleteOpenCodeArchiveAsync(archive, cancellationToken);
            return;
        }
        if (archive.Kind is not ("file" or "directory" or "codex")) return;
        if (File.Exists(archive.BackupPath))
        {
            File.Delete(archive.BackupPath);
        }
        else if (Directory.Exists(archive.BackupPath))
        {
            Directory.Delete(archive.BackupPath, true);
        }
    }

    public async Task<NativeAgentWriteResult> WriteAsync(
        WebDavConversationDetail conversation,
        string target,
        CancellationToken cancellationToken = default)
    {
        return target.ToLowerInvariant() switch
        {
            "claude" => await WriteClaudeAsync(conversation, cancellationToken),
            "codex" => await WriteCodexAsync(conversation, cancellationToken),
            "gemini" => await WriteGeminiAsync(conversation, cancellationToken),
            "opencode" => await WriteOpenCodeAsync(conversation, cancellationToken),
            _ => throw new NotSupportedException(
                $"{target} 的原生会话格式不支持安全写入。"),
        };
    }

    public async Task DiscardAsync(
        NativeAgentWriteResult result,
        string target,
        CancellationToken cancellationToken = default)
    {
        switch (target.ToLowerInvariant())
        {
            case "claude":
            case "gemini":
                File.Delete(result.StoragePath);
                return;
            case "codex":
                File.Delete(result.StoragePath);
                var codexDatabase = Path.Combine(_home, ".codex", "state_5.sqlite");
                if (!File.Exists(codexDatabase)) return;
                await using (var connection = new SqliteConnection(
                                 $"Data Source={codexDatabase}"))
                {
                    await connection.OpenAsync(cancellationToken);
                    var command = connection.CreateCommand();
                    command.CommandText = "DELETE FROM threads WHERE id=$id;";
                    command.Parameters.AddWithValue("$id", result.Id);
                    await command.ExecuteNonQueryAsync(cancellationToken);
                }
                return;
            case "opencode":
                var openCodeDatabase = FindOpenCodeDatabase();
                await using (var connection = new SqliteConnection(
                                 $"Data Source={openCodeDatabase}"))
                {
                    await connection.OpenAsync(cancellationToken);
                    await using var transaction =
                        await connection.BeginTransactionAsync(cancellationToken);
                    foreach (var table in new[] { "part", "message" })
                    {
                        var command = connection.CreateCommand();
                        command.Transaction = (SqliteTransaction)transaction;
                        command.CommandText =
                            $"DELETE FROM {table} WHERE session_id=$id;";
                        command.Parameters.AddWithValue("$id", result.Id);
                        await command.ExecuteNonQueryAsync(cancellationToken);
                    }
                    var session = connection.CreateCommand();
                    session.Transaction = (SqliteTransaction)transaction;
                    session.CommandText = "DELETE FROM session WHERE id=$id;";
                    session.Parameters.AddWithValue("$id", result.Id);
                    await session.ExecuteNonQueryAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                }
                return;
        }
    }

    private async Task<NativeAgentWriteResult> WriteClaudeAsync(
        WebDavConversationDetail conversation,
        CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid().ToString().ToLowerInvariant();
        var project = NormalizeProject(conversation.ProjectDir);
        var encoded = string.Concat(project.Select(character =>
            """/\:<>\"|?*""".Contains(character)
                || char.IsControl(character) ? '-' : character));
        var directory = Path.Combine(_home, ".claude", "projects", encoded);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{id}.jsonl");
        var lines = new List<string>();
        if (conversation.FileChanges.Count > 0)
        {
            lines.Add(JsonSerializer.Serialize(new
            {
                type = "file-history-snapshot",
                snapshot = new
                {
                    trackedFileBackups = conversation.FileChanges
                        .Select(change => change.Path)
                        .Distinct()
                        .ToDictionary(
                            path => path,
                            _ => (object)new
                            {
                                backupFileName = (string?)null,
                                version = 1,
                                backupTime = conversation.UpdatedAt,
                            }),
                    timestamp = conversation.CreatedAt,
                },
            }));
        }
        string? parent = null;
        foreach (var message in conversation.Messages)
        {
            var eventId = Guid.NewGuid().ToString().ToLowerInvariant();
            var assistant = message.Role.Equals(
                "assistant", StringComparison.OrdinalIgnoreCase);
            object content = message.Content;
            var toolResults = new List<(string Id, WebDavToolCall Tool)>();
            if (assistant && message.ToolCalls.Count > 0)
            {
                var blocks = new List<Dictionary<string, object?>>();
                if (!string.IsNullOrEmpty(message.Content))
                {
                    blocks.Add(new Dictionary<string, object?>
                    {
                        ["type"] = "text",
                        ["text"] = message.Content,
                    });
                }
                foreach (var tool in message.ToolCalls)
                {
                    var toolId = $"toolu_{Guid.NewGuid():N}";
                    blocks.Add(new Dictionary<string, object?>
                    {
                        ["type"] = "tool_use",
                        ["id"] = toolId,
                        ["name"] = tool.Name,
                        ["input"] = tool.Input,
                    });
                    toolResults.Add((toolId, tool));
                }
                content = blocks;
            }
            var value = new Dictionary<string, object?>
            {
                ["type"] = assistant ? "assistant" : "user",
                ["uuid"] = eventId,
                ["timestamp"] = message.Timestamp,
                ["sessionId"] = id,
                ["cwd"] = project,
                ["isSidechain"] = false,
                ["message"] = new Dictionary<string, object?>
                {
                    ["role"] = assistant ? "assistant" : "user",
                    ["content"] = content,
                },
            };
            if (parent is not null) value["parentUuid"] = parent;
            lines.Add(JsonSerializer.Serialize(value));
            parent = eventId;
            foreach (var (toolId, tool) in toolResults)
            {
                var resultId = Guid.NewGuid().ToString().ToLowerInvariant();
                lines.Add(JsonSerializer.Serialize(new
                {
                    type = "user",
                    uuid = resultId,
                    parentUuid = parent,
                    timestamp = message.Timestamp,
                    sessionId = id,
                    cwd = project,
                    isSidechain = false,
                    message = new
                    {
                        role = "user",
                        content = new[]
                        {
                            new
                            {
                                type = "tool_result",
                                tool_use_id = toolId,
                                content = tool.Output ?? "",
                                is_error = tool.Status.Equals(
                                    "error",
                                    StringComparison.OrdinalIgnoreCase),
                            },
                        },
                    },
                }));
                parent = resultId;
            }
        }
        if (!string.IsNullOrWhiteSpace(conversation.Summary))
        {
            lines.Add(JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["type"] = "summary",
                ["summary"] = conversation.Summary,
                ["leafUuid"] = parent ?? Guid.NewGuid().ToString(),
            }));
        }
        await File.WriteAllLinesAsync(path, lines, cancellationToken);
        return new NativeAgentWriteResult(
            id, path, $"claude --resume {id}");
    }

    private async Task<NativeAgentWriteResult> WriteGeminiAsync(
        WebDavConversationDetail conversation,
        CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid().ToString().ToLowerInvariant();
        var project = NormalizeProject(conversation.ProjectDir);
        var projectHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(project))).ToLowerInvariant();
        var directory = Path.Combine(
            _home, ".gemini", "tmp", projectHash, "chats");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"session-{id}.json");
        var messages = conversation.Messages.Select(message =>
        {
            var assistant = message.Role.Equals(
                "assistant", StringComparison.OrdinalIgnoreCase);
            return new Dictionary<string, object?>
            {
                ["id"] = Guid.NewGuid().ToString().ToLowerInvariant(),
                ["timestamp"] = message.Timestamp,
                ["type"] = assistant ? "gemini" : "user",
                ["content"] = message.Content,
                ["model"] = assistant ? "imported" : null,
                ["toolCalls"] = assistant
                    ? message.ToolCalls.Select(tool => new
                    {
                        id = Guid.NewGuid().ToString().ToLowerInvariant(),
                        name = tool.Name,
                        args = tool.Input,
                        resultDisplay = tool.Output ?? "",
                        status = tool.Status.Equals(
                            "error", StringComparison.OrdinalIgnoreCase)
                            ? "error" : "success",
                    }).ToArray()
                    : null,
            };
        }).ToArray();
        var value = new Dictionary<string, object?>
        {
            ["sessionId"] = id,
            ["projectHash"] = projectHash,
            ["projectPath"] = project,
            ["startTime"] = conversation.CreatedAt,
            ["lastUpdated"] = conversation.UpdatedAt,
            ["summary"] = conversation.Summary,
            ["messages"] = messages,
            ["fileChanges"] = conversation.FileChanges,
        };
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine,
            cancellationToken);
        return new NativeAgentWriteResult(id, path, $"gemini --resume {id}");
    }

    private async Task<NativeAgentWriteResult> WriteCodexAsync(
        WebDavConversationDetail conversation,
        CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid().ToString().ToLowerInvariant();
        var project = NormalizeProject(conversation.ProjectDir);
        var created = ParseDate(conversation.CreatedAt);
        var directory = Path.Combine(
            _home, ".codex", "sessions",
            created.Year.ToString("0000"),
            created.Month.ToString("00"),
            created.Day.ToString("00"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(
            directory,
            $"rollout-{created:yyyy-MM-ddTHH-mm-ss}-{id}.jsonl");
        var lines = new List<string>
        {
            JsonSerializer.Serialize(new
            {
                timestamp = conversation.CreatedAt,
                type = "session_meta",
                payload = new
                {
                    id,
                    timestamp = conversation.CreatedAt,
                    cwd = project,
                    originator = "AI Memory",
                    cli_version = "",
                    source = "vscode",
                    model_provider = "openai",
                    aimemory_file_changes = conversation.FileChanges,
                },
            }),
        };
        var firstUser = "";
        foreach (var message in conversation.Messages)
        {
            var assistant = message.Role.Equals(
                "assistant", StringComparison.OrdinalIgnoreCase);
            if (!assistant && firstUser.Length == 0)
            {
                firstUser = message.Content;
            }
            lines.Add(JsonSerializer.Serialize(new
            {
                timestamp = message.Timestamp,
                type = "event_msg",
                payload = new
                {
                    type = assistant ? "agent_message" : "user_message",
                    message = message.Content,
                },
            }));
            if (assistant)
            {
                foreach (var tool in message.ToolCalls)
                {
                    var callId = $"call_{Guid.NewGuid():N}";
                    lines.Add(JsonSerializer.Serialize(new
                    {
                        timestamp = message.Timestamp,
                        type = "response_item",
                        payload = new
                        {
                            type = "function_call",
                            name = tool.Name,
                            arguments = tool.Input.GetRawText(),
                            call_id = callId,
                        },
                    }));
                    lines.Add(JsonSerializer.Serialize(new
                    {
                        timestamp = message.Timestamp,
                        type = "response_item",
                        payload = new
                        {
                            type = "function_call_output",
                            call_id = callId,
                            output = tool.Output ?? "",
                        },
                    }));
                }
            }
        }
        await File.WriteAllLinesAsync(path, lines, cancellationToken);

        var databasePath = Path.Combine(_home, ".codex", "state_5.sqlite");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        try
        {
            await using var connection = new SqliteConnection(
                $"Data Source={databasePath}");
            await connection.OpenAsync(cancellationToken);
            await EnsureCodexSchemaAsync(connection, cancellationToken);
            var createdSeconds = created.ToUnixTimeSeconds();
            var updated = ParseDate(conversation.UpdatedAt);
            await InsertDynamicAsync(
                connection,
                "threads",
                new Dictionary<string, object?>
                {
                    ["id"] = id,
                    ["rollout_path"] = path,
                    ["created_at"] = createdSeconds,
                    ["updated_at"] = updated.ToUnixTimeSeconds(),
                    ["source"] = "vscode",
                    ["model_provider"] = "openai",
                    ["cwd"] = project,
                    ["title"] = Title(conversation),
                    ["sandbox_policy"] = """{"type":"workspace-write"}""",
                    ["approval_mode"] = "on-request",
                    ["tokens_used"] = 0,
                    ["has_user_event"] = firstUser.Length == 0 ? 0 : 1,
                    ["archived"] = 0,
                    ["cli_version"] = "",
                    ["first_user_message"] = firstUser,
                    ["memory_mode"] = "enabled",
                    ["created_at_ms"] = created.ToUnixTimeMilliseconds(),
                    ["updated_at_ms"] = updated.ToUnixTimeMilliseconds(),
                    ["thread_source"] = "user",
                    ["preview"] = firstUser.Length == 0
                        ? Title(conversation) : firstUser,
                },
                cancellationToken);
        }
        catch
        {
            File.Delete(path);
            throw;
        }
        return new NativeAgentWriteResult(id, path, $"codex resume {id}");
    }

    private async Task<NativeAgentWriteResult> WriteOpenCodeAsync(
        WebDavConversationDetail conversation,
        CancellationToken cancellationToken)
    {
        var databasePath = FindOpenCodeDatabase();
        var id = CompactId("ses");
        var project = NormalizeProject(conversation.ProjectDir);
        var created = ParseDate(conversation.CreatedAt).ToUnixTimeMilliseconds();
        var updated = ParseDate(conversation.UpdatedAt).ToUnixTimeMilliseconds();
        await using var connection = new SqliteConnection(
            $"Data Source={databasePath}");
        await connection.OpenAsync(cancellationToken);
        foreach (var table in new[] { "session", "message", "part", "project" })
        {
            if (!await TableExistsAsync(connection, table, cancellationToken))
            {
                throw new InvalidDataException(
                    $"OpenCode 数据库缺少 {table} 表。");
            }
        }
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var projectId = await FindOrCreateOpenCodeProjectAsync(
                connection,
                (SqliteTransaction)transaction,
                project,
                created,
                cancellationToken);
            await InsertDynamicAsync(
                connection,
                "session",
                new Dictionary<string, object?>
                {
                    ["id"] = id,
                    ["project_id"] = projectId,
                    ["slug"] = Slug(Title(conversation)),
                    ["directory"] = project,
                    ["title"] = Title(conversation),
                    ["version"] = "0.0.0",
                    ["summary_files"] = conversation.FileChanges
                        .Select(value => value.Path).Distinct().Count(),
                    ["time_created"] = created,
                    ["time_updated"] = updated,
                    ["time_archived"] = null,
                },
                cancellationToken,
                (SqliteTransaction)transaction);

            string? parentId = null;
            foreach (var message in conversation.Messages)
            {
                var messageId = CompactId("msg");
                var timestamp = ParseDate(message.Timestamp).ToUnixTimeMilliseconds();
                await InsertDynamicAsync(
                    connection,
                    "message",
                    new Dictionary<string, object?>
                    {
                        ["id"] = messageId,
                        ["session_id"] = id,
                        ["time_created"] = timestamp,
                        ["time_updated"] = timestamp,
                        ["data"] = JsonSerializer.Serialize(new
                        {
                            role = NormalizeRole(message.Role),
                            parentID = parentId,
                            source = "aimemory",
                            path = new { cwd = project, root = project },
                        }),
                    },
                    cancellationToken,
                    (SqliteTransaction)transaction);
                if (!string.IsNullOrWhiteSpace(message.Content))
                {
                    await InsertOpenCodePartAsync(
                        connection,
                        (SqliteTransaction)transaction,
                        id,
                        messageId,
                        timestamp,
                        new { type = "text", text = message.Content },
                        cancellationToken);
                }
                foreach (var tool in message.ToolCalls)
                {
                    await InsertOpenCodePartAsync(
                        connection,
                        (SqliteTransaction)transaction,
                        id,
                        messageId,
                        timestamp,
                        new
                        {
                            type = "tool",
                            callID = CompactId("call"),
                            tool = tool.Name,
                            state = new
                            {
                                status = tool.Status.Equals(
                                    "error", StringComparison.OrdinalIgnoreCase)
                                    ? "error" : "completed",
                                input = tool.Input,
                                output = tool.Output,
                            },
                        },
                        cancellationToken);
                }
                var changedFiles = conversation.FileChanges
                    .Where(change => string.Equals(
                        change.MessageId,
                        message.Id,
                        StringComparison.Ordinal))
                    .Select(change => change.Path)
                    .Distinct()
                    .ToArray();
                if (changedFiles.Length > 0)
                {
                    await InsertOpenCodePartAsync(
                        connection,
                        (SqliteTransaction)transaction,
                        id,
                        messageId,
                        timestamp,
                        new
                        {
                            type = "patch",
                            hash = CompactId("patch"),
                            files = changedFiles,
                        },
                        cancellationToken);
                }
                parentId = messageId;
            }
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
        return new NativeAgentWriteResult(
            id, databasePath, $"opencode --session {id}");
    }

    private NativeSourceArchive ArchiveFileBackedSource(
        WebDavConversationDetail conversation)
    {
        if (string.IsNullOrWhiteSpace(conversation.StoragePath))
        {
            throw new InvalidDataException(
                $"{conversation.SourceAgent} 会话缺少原始存储路径。");
        }
        var original = conversation.StoragePath;
        if (conversation.SourceAgent.Equals(
                "kimi", StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                Path.GetFileName(original),
                "state.json",
                StringComparison.OrdinalIgnoreCase))
        {
            original = Path.GetDirectoryName(original)
                ?? throw new InvalidDataException("Kimi 会话目录无效。");
        }
        var isDirectory = Directory.Exists(original);
        if (!isDirectory && !File.Exists(original))
        {
            throw new FileNotFoundException(
                "原始 Agent 会话不存在。", original);
        }
        Directory.CreateDirectory(_archiveRoot);
        var backup = Path.Combine(
            _archiveRoot,
            $"{conversation.SourceAgent}-{conversation.Id}-{Guid.NewGuid():N}"
            + (isDirectory ? "" : Path.GetExtension(original)));
        if (isDirectory)
        {
            Directory.Move(original, backup);
        }
        else
        {
            File.Move(original, backup);
        }
        return new NativeSourceArchive(
            conversation.SourceAgent,
            conversation.Id,
            isDirectory ? "directory" : "file",
            original,
            backup,
            null,
            new Dictionary<string, string?>());
    }

    private async Task<NativeSourceArchive> ArchiveCodexAsync(
        string conversationId,
        CancellationToken cancellationToken)
    {
        var databasePath = Path.Combine(_home, ".codex", "state_5.sqlite");
        if (!File.Exists(databasePath))
        {
            throw new FileNotFoundException(
                "Codex 状态数据库不存在。", databasePath);
        }
        await using var connection = new SqliteConnection(
            $"Data Source={databasePath}");
        await connection.OpenAsync(cancellationToken);
        var select = connection.CreateCommand();
        select.CommandText = "SELECT * FROM threads WHERE id=$id LIMIT 1;";
        select.Parameters.AddWithValue("$id", conversationId);
        var metadata = new Dictionary<string, string?>(
            StringComparer.OrdinalIgnoreCase);
        string rolloutPath;
        await using (var reader = await select.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidDataException(
                    $"Codex 会话不存在：{conversationId}");
            }
            for (var index = 0; index < reader.FieldCount; index++)
            {
                metadata[reader.GetName(index)] = reader.IsDBNull(index)
                    ? null
                    : Convert.ToString(reader.GetValue(index));
            }
            rolloutPath = metadata.GetValueOrDefault("rollout_path")
                ?? throw new InvalidDataException(
                    "Codex 会话缺少 rollout_path。");
        }
        if (!File.Exists(rolloutPath))
        {
            throw new FileNotFoundException(
                "Codex rollout 文件不存在。", rolloutPath);
        }
        Directory.CreateDirectory(_archiveRoot);
        var backup = Path.Combine(
            _archiveRoot,
            $"codex-{conversationId}-{Guid.NewGuid():N}.jsonl");
        File.Move(rolloutPath, backup);
        try
        {
            var delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM threads WHERE id=$id;";
            delete.Parameters.AddWithValue("$id", conversationId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
        catch
        {
            File.Move(backup, rolloutPath);
            throw;
        }
        return new NativeSourceArchive(
            "codex",
            conversationId,
            "codex",
            rolloutPath,
            backup,
            databasePath,
            metadata);
    }

    private async Task<NativeSourceArchive> ArchiveOpenCodeAsync(
        string conversationId,
        CancellationToken cancellationToken)
    {
        var databasePath = FindOpenCodeDatabase();
        await SetOpenCodeArchivedAsync(
            databasePath,
            conversationId,
            archived: true,
            cancellationToken);
        return new NativeSourceArchive(
            "opencode",
            conversationId,
            "opencode",
            "",
            "",
            databasePath,
            new Dictionary<string, string?>());
    }

    private static async Task SetOpenCodeArchivedAsync(
        string databasePath,
        string conversationId,
        bool archived,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(
            $"Data Source={databasePath}");
        await connection.OpenAsync(cancellationToken);
        var columns = await ColumnsAsync(
            connection, "session", cancellationToken);
        if (!columns.Contains("time_archived"))
        {
            throw new InvalidDataException(
                "OpenCode session 表缺少 time_archived 字段。");
        }
        var command = connection.CreateCommand();
        command.CommandText = archived
            ? "UPDATE session SET time_archived=$time WHERE id=$id;"
            : "UPDATE session SET time_archived=NULL WHERE id=$id;";
        command.Parameters.AddWithValue(
            "$time", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$id", conversationId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            throw new InvalidDataException(
                $"OpenCode 会话不存在：{conversationId}");
        }
    }

    private static async Task DeleteOpenCodeArchiveAsync(
        NativeSourceArchive archive,
        CancellationToken cancellationToken)
    {
        var databasePath = archive.DatabasePath
            ?? throw new InvalidDataException(
                "OpenCode 归档缺少数据库路径。");
        if (!File.Exists(databasePath))
        {
            throw new FileNotFoundException(
                "OpenCode 状态数据库不存在。", databasePath);
        }
        await using var connection = new SqliteConnection(
            $"Data Source={databasePath}");
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        foreach (var table in new[] { "part", "message", "session" })
        {
            if (!await TableExistsAsync(
                    connection, table, cancellationToken))
            {
                continue;
            }
            var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText =
                $"DELETE FROM {table} WHERE "
                + (table == "session" ? "id" : "session_id")
                + "=$id;";
            command.Parameters.AddWithValue(
                "$id", archive.ConversationId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task RestoreCodexArchiveAsync(
        NativeSourceArchive archive,
        CancellationToken cancellationToken)
    {
        var databasePath = archive.DatabasePath
            ?? throw new InvalidDataException("Codex 归档缺少数据库路径。");
        if (File.Exists(archive.OriginalPath))
        {
            throw new IOException(
                $"原位置已有文件，拒绝覆盖：{archive.OriginalPath}");
        }
        if (!File.Exists(archive.BackupPath))
        {
            throw new FileNotFoundException(
                "Codex 原始历史归档不存在。", archive.BackupPath);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(archive.OriginalPath)!);
        File.Move(archive.BackupPath, archive.OriginalPath);
        try
        {
            await using var connection = new SqliteConnection(
                $"Data Source={databasePath}");
            await connection.OpenAsync(cancellationToken);
            await InsertDynamicAsync(
                connection,
                "threads",
                archive.Metadata.ToDictionary(
                    value => value.Key,
                    value => (object?)value.Value,
                    StringComparer.OrdinalIgnoreCase),
                cancellationToken);
        }
        catch
        {
            File.Move(archive.OriginalPath, archive.BackupPath);
            throw;
        }
    }

    private static void RestoreMovedPath(
        NativeSourceArchive archive,
        bool directory)
    {
        if (File.Exists(archive.OriginalPath)
            || Directory.Exists(archive.OriginalPath))
        {
            throw new IOException(
                $"原位置已有内容，拒绝覆盖：{archive.OriginalPath}");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(archive.OriginalPath)!);
        if (directory)
        {
            if (!Directory.Exists(archive.BackupPath))
            {
                throw new DirectoryNotFoundException(
                    $"原始历史归档不存在：{archive.BackupPath}");
            }
            Directory.Move(archive.BackupPath, archive.OriginalPath);
        }
        else
        {
            if (!File.Exists(archive.BackupPath))
            {
                throw new FileNotFoundException(
                    "原始历史归档不存在。", archive.BackupPath);
            }
            File.Move(archive.BackupPath, archive.OriginalPath);
        }
    }

    private static async Task EnsureCodexSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS threads (
              id TEXT PRIMARY KEY, rollout_path TEXT NOT NULL,
              created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL,
              source TEXT NOT NULL, model_provider TEXT NOT NULL, cwd TEXT NOT NULL,
              title TEXT NOT NULL, sandbox_policy TEXT NOT NULL,
              approval_mode TEXT NOT NULL, tokens_used INTEGER NOT NULL DEFAULT 0,
              has_user_event INTEGER NOT NULL DEFAULT 0,
              archived INTEGER NOT NULL DEFAULT 0,
              cli_version TEXT NOT NULL DEFAULT '',
              first_user_message TEXT NOT NULL DEFAULT '',
              memory_mode TEXT NOT NULL DEFAULT 'enabled',
              created_at_ms INTEGER, updated_at_ms INTEGER,
              thread_source TEXT, preview TEXT
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<string> FindOrCreateOpenCodeProjectAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string project,
        long timestamp,
        CancellationToken cancellationToken)
    {
        var columns = await ColumnsAsync(
            connection, "project", cancellationToken, transaction);
        if (columns.Contains("worktree"))
        {
            var find = connection.CreateCommand();
            find.Transaction = transaction;
            find.CommandText =
                "SELECT id FROM project WHERE worktree=$path LIMIT 1;";
            find.Parameters.AddWithValue("$path", project);
            if (await find.ExecuteScalarAsync(cancellationToken) is string existing)
            {
                return existing;
            }
        }
        var id = CompactId("project");
        await InsertDynamicAsync(
            connection,
            "project",
            new Dictionary<string, object?>
            {
                ["id"] = id,
                ["worktree"] = project,
                ["vcs"] = "git",
                ["name"] = Path.GetFileName(project.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar)),
                ["time_created"] = timestamp,
                ["time_updated"] = timestamp,
                ["sandboxes"] = "[]",
            },
            cancellationToken,
            transaction);
        return id;
    }

    private static Task InsertOpenCodePartAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        string messageId,
        long timestamp,
        object data,
        CancellationToken cancellationToken) =>
        InsertDynamicAsync(
            connection,
            "part",
            new Dictionary<string, object?>
            {
                ["id"] = CompactId("part"),
                ["message_id"] = messageId,
                ["session_id"] = sessionId,
                ["time_created"] = timestamp,
                ["time_updated"] = timestamp,
                ["data"] = JsonSerializer.Serialize(data),
            },
            cancellationToken,
            transaction);

    private static async Task InsertDynamicAsync(
        SqliteConnection connection,
        string table,
        IReadOnlyDictionary<string, object?> values,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        var columns = await ColumnsAsync(
            connection, table, cancellationToken, transaction);
        var selected = values
            .Where(value => columns.Contains(value.Key))
            .ToArray();
        if (selected.Length == 0)
        {
            throw new InvalidDataException($"{table} 表没有兼容字段。");
        }
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"INSERT INTO {table} ({string.Join(",", selected.Select(value => value.Key))}) "
            + $"VALUES ({string.Join(",", selected.Select((_, index) => $"$p{index}"))});";
        for (var index = 0; index < selected.Length; index++)
        {
            command.Parameters.AddWithValue(
                $"$p{index}",
                selected[index].Value ?? DBNull.Value);
        }
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<HashSet<string>> ColumnsAsync(
        SqliteConnection connection,
        string table,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info({table});";
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetString(1));
        }
        return result;
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name;";
        command.Parameters.AddWithValue("$name", table);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    private string FindOpenCodeDatabase()
    {
        var candidates = new[]
        {
            Path.Combine(_home, ".local", "share", "opencode", "opencode.db"),
            Path.Combine(_home, ".local", "share", "opencode", "opencode.sqlite"),
            Path.Combine(_home, ".config", "opencode", "opencode.db"),
            Path.Combine(_home, "AppData", "Local", "opencode", "opencode.db"),
            Path.Combine(_home, "AppData", "Roaming", "opencode", "opencode.db"),
        };
        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException(
                "目标 OpenCode 尚未创建本地数据库。",
                candidates[0]);
    }

    private string NormalizeProject(string value) =>
        string.IsNullOrWhiteSpace(value) || value == "."
            ? _home : value.Trim();

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.TryParse(value, out var parsed)
            ? parsed : DateTimeOffset.UtcNow;

    private static string NormalizeRole(string role) =>
        role.Equals("assistant", StringComparison.OrdinalIgnoreCase)
            ? "assistant"
            : role.Equals("system", StringComparison.OrdinalIgnoreCase)
                ? "system" : "user";

    private static string Title(WebDavConversationDetail conversation)
    {
        var value = !string.IsNullOrWhiteSpace(conversation.Summary)
            ? conversation.Summary
            : conversation.Messages.FirstOrDefault(message =>
                message.Role.Equals("user", StringComparison.OrdinalIgnoreCase))
                ?.Content;
        return string.IsNullOrWhiteSpace(value)
            ? "AI Memory imported conversation"
            : value[..Math.Min(value.Length, 80)];
    }

    private static string Slug(string value)
    {
        var output = string.Join(
            "-",
            new string(value.ToLowerInvariant().Select(character =>
                char.IsLetterOrDigit(character) ? character : '-').ToArray())
                .Split('-', StringSplitOptions.RemoveEmptyEntries));
        return output.Length == 0 ? "aimemory-import" : output;
    }

    private static string CompactId(string prefix) =>
        $"{prefix}_{Guid.NewGuid():N}".ToLowerInvariant();
}
