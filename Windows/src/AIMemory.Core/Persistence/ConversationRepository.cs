using AIMemory.Core.Models;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AIMemory.Core.Persistence;

public sealed class ConversationRepository(AIMemoryDatabase database)
{
    public async Task<IReadOnlyList<ConversationSummary>> ListAsync(
        string? sourceAgent = null,
        string? search = null,
        int limit = 500,
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.OpenConnection();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT conversation_id, repo_id, source_agent, source_conversation_id,
                   COALESCE(summary, ''), started_at, updated_at, storage_path
            FROM conversations
            WHERE ($agent IS NULL OR source_agent = $agent)
              AND ($search IS NULL OR summary LIKE '%' || $search || '%'
                   OR repo_id LIKE '%' || $search || '%')
            ORDER BY updated_at DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$agent", (object?)sourceAgent ?? DBNull.Value);
        command.Parameters.AddWithValue("$search", (object?)search ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 5_000));

        var result = new List<ConversationSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ConversationSummary(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                ParseDate(reader.GetString(5)),
                ParseDate(reader.GetString(6)),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }
        return result;
    }

    public async Task<IReadOnlyList<ConversationMessage>> ReadMessagesAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.OpenConnection();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT message_id, conversation_id, role, content, timestamp
            FROM messages
            WHERE conversation_id = $id
            ORDER BY timestamp, rowid;
            """;
        command.Parameters.AddWithValue("$id", conversationId);

        var result = new List<ConversationMessage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ConversationMessage(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                ParseDate(reader.GetString(4))));
        }
        return result;
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = database.OpenConnection();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM conversations;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task<WebDavConversationDetail> ExportAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.OpenConnection();
        var rootCommand = connection.CreateCommand();
        rootCommand.CommandText = """
            SELECT c.conversation_id, c.source_agent, COALESCE(r.repo_root, ''),
                   c.started_at, c.updated_at, c.summary, c.storage_path
            FROM conversations c
            LEFT JOIN repos r ON r.repo_id=c.repo_id
            WHERE c.conversation_id=$id LIMIT 1;
            """;
        rootCommand.Parameters.AddWithValue("$id", conversationId);
        await using var root = await rootCommand.ExecuteReaderAsync(cancellationToken);
        if (!await root.ReadAsync(cancellationToken))
        {
            throw new KeyNotFoundException($"找不到对话 {conversationId}。");
        }
        var id = root.GetString(0);
        var agent = root.GetString(1);
        var projectDir = root.GetString(2);
        var createdAt = root.GetString(3);
        var updatedAt = root.GetString(4);
        var summary = root.IsDBNull(5) ? null : root.GetString(5);
        var storagePath = root.IsDBNull(6) ? null : root.GetString(6);
        await root.CloseAsync();

        var messageCommand = connection.CreateCommand();
        messageCommand.CommandText = """
            SELECT message_id, timestamp, role, content
            FROM messages WHERE conversation_id=$id
            ORDER BY timestamp, rowid;
            """;
        messageCommand.Parameters.AddWithValue("$id", conversationId);
        var messageRows = new List<(string Id, string Timestamp, string Role, string Content)>();
        await using (var reader = await messageCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                messageRows.Add((
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3)));
            }
        }
        var messages = new List<WebDavMessage>();
        foreach (var message in messageRows)
        {
            messages.Add(new WebDavMessage(
                message.Id,
                message.Timestamp,
                message.Role,
                message.Content,
                await ReadToolsAsync(connection, message.Id, cancellationToken),
                []));
        }

        var changeCommand = connection.CreateCommand();
        changeCommand.CommandText = """
            SELECT path, change_type, timestamp, message_id
            FROM file_changes WHERE conversation_id=$id
            ORDER BY timestamp, rowid;
            """;
        changeCommand.Parameters.AddWithValue("$id", conversationId);
        var changes = new List<WebDavFileChange>();
        await using (var reader = await changeCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                changes.Add(new WebDavFileChange(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3)));
            }
        }
        return new WebDavConversationDetail(
            id, agent, projectDir, createdAt, updatedAt, summary, storagePath,
            ResumeCommand(agent, id), messages, changes);
    }

    public async Task UpsertAsync(
        WebDavConversationDetail detail,
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.OpenConnection();
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var repoRoot = string.IsNullOrWhiteSpace(detail.ProjectDir)
            ? $"aimemory://unscoped/{detail.SourceAgent}"
            : detail.ProjectDir;
        var repoId = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(repoRoot))).ToLowerInvariant();

        var repo = connection.CreateCommand();
        repo.Transaction = (SqliteTransaction)transaction;
        repo.CommandText = """
            INSERT INTO repos(
              repo_id, repo_root, repo_fingerprint, created_at, updated_at)
            VALUES($id,$root,$fingerprint,$now,$now)
            ON CONFLICT(repo_root) DO UPDATE SET updated_at=excluded.updated_at;
            """;
        repo.Parameters.AddWithValue("$id", repoId);
        repo.Parameters.AddWithValue("$root", repoRoot);
        repo.Parameters.AddWithValue("$fingerprint", repoId);
        repo.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await repo.ExecuteNonQueryAsync(cancellationToken);

        var clear = connection.CreateCommand();
        clear.Transaction = (SqliteTransaction)transaction;
        clear.CommandText = """
            DELETE FROM tool_calls WHERE message_id IN (
              SELECT message_id FROM messages WHERE conversation_id=$id);
            DELETE FROM messages WHERE conversation_id=$id;
            DELETE FROM file_changes WHERE conversation_id=$id;
            INSERT INTO conversations(
              conversation_id,repo_id,source_agent,source_conversation_id,
              summary,started_at,updated_at,storage_path)
            VALUES($id,$repo,$agent,$source,$summary,$started,$updated,$path)
            ON CONFLICT(conversation_id) DO UPDATE SET
              repo_id=excluded.repo_id, source_agent=excluded.source_agent,
              summary=excluded.summary, started_at=excluded.started_at,
              updated_at=excluded.updated_at, storage_path=excluded.storage_path;
            """;
        clear.Parameters.AddWithValue("$id", detail.Id);
        clear.Parameters.AddWithValue("$repo", repoId);
        clear.Parameters.AddWithValue("$agent", detail.SourceAgent);
        clear.Parameters.AddWithValue("$source", detail.Id);
        clear.Parameters.AddWithValue("$summary", (object?)detail.Summary ?? "");
        clear.Parameters.AddWithValue("$started", detail.CreatedAt);
        clear.Parameters.AddWithValue("$updated", detail.UpdatedAt);
        clear.Parameters.AddWithValue("$path", (object?)detail.StoragePath ?? "");
        await clear.ExecuteNonQueryAsync(cancellationToken);

        foreach (var message in detail.Messages)
        {
            var insert = connection.CreateCommand();
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = """
                INSERT INTO messages VALUES($id,$conversation,$role,$content,$timestamp);
                """;
            insert.Parameters.AddWithValue("$id", message.Id);
            insert.Parameters.AddWithValue("$conversation", detail.Id);
            insert.Parameters.AddWithValue("$role", message.Role);
            insert.Parameters.AddWithValue("$content", message.Content);
            insert.Parameters.AddWithValue("$timestamp", message.Timestamp);
            await insert.ExecuteNonQueryAsync(cancellationToken);
            foreach (var tool in message.ToolCalls)
            {
                var toolInsert = connection.CreateCommand();
                toolInsert.Transaction = (SqliteTransaction)transaction;
                toolInsert.CommandText = """
                    INSERT INTO tool_calls VALUES(
                      $id,$message,$name,$input,$output,$status);
                    """;
                toolInsert.Parameters.AddWithValue("$id", tool.Id);
                toolInsert.Parameters.AddWithValue("$message", message.Id);
                toolInsert.Parameters.AddWithValue("$name", tool.Name);
                toolInsert.Parameters.AddWithValue("$input", tool.Input.GetRawText());
                toolInsert.Parameters.AddWithValue("$output", (object?)tool.Output ?? "");
                toolInsert.Parameters.AddWithValue("$status", tool.Status);
                await toolInsert.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        foreach (var change in detail.FileChanges)
        {
            var insert = connection.CreateCommand();
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = """
                INSERT INTO file_changes VALUES(
                  $id,$conversation,$message,$path,$type,$timestamp);
                """;
            insert.Parameters.AddWithValue(
                "$id",
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                    $"{detail.Id}|{change.Path}|{change.ChangeType}|{change.Timestamp}")))
                    .ToLowerInvariant());
            insert.Parameters.AddWithValue("$conversation", detail.Id);
            insert.Parameters.AddWithValue("$message", (object?)change.MessageId ?? "");
            insert.Parameters.AddWithValue("$path", change.Path);
            insert.Parameters.AddWithValue("$type", change.ChangeType);
            insert.Parameters.AddWithValue("$timestamp", change.Timestamp);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<WebDavToolCall>> ReadToolsAsync(
        SqliteConnection connection,
        string messageId,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT tool_call_id,name,input_json,output_text,status
            FROM tool_calls WHERE message_id=$id ORDER BY rowid;
            """;
        command.Parameters.AddWithValue("$id", messageId);
        var result = new List<WebDavToolCall>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            JsonElement input;
            try
            {
                using var document = JsonDocument.Parse(reader.GetString(2));
                input = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                input = JsonSerializer.SerializeToElement<object?>(null);
            }
            result.Add(new WebDavToolCall(
                reader.GetString(0),
                reader.GetString(1),
                input,
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4)));
        }
        return result;
    }

    private static string? ResumeCommand(string agent, string id) =>
        agent.ToLowerInvariant() switch
        {
            "claude" => $"claude --resume {id}",
            "codex" => $"codex resume {id}",
            "gemini" => $"gemini --resume {id}",
            _ => null,
        };

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.TryParse(value, out var parsed)
            ? parsed
            : DateTimeOffset.UnixEpoch;
}
