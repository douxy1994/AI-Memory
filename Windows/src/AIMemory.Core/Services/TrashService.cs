using System.Text.Json;
using AIMemory.Core.Models;
using AIMemory.Core.Persistence;

namespace AIMemory.Core.Services;

public sealed class TrashService(
    AIMemoryDatabase database,
    string? trashDirectory = null,
    Func<DateTimeOffset>? now = null,
    NativeAgentConversationWriter? writer = null)
{
    private readonly string _trashDirectory =
        trashDirectory ?? DataPaths.TrashDirectory;
    private readonly Func<DateTimeOffset> _now =
        now ?? (() => DateTimeOffset.UtcNow);
    private readonly NativeAgentConversationWriter _writer =
        writer ?? new NativeAgentConversationWriter();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public async Task<TrashRecord> TrashAsync(
        ConversationSummary conversation,
        int retentionDays,
        NativeSourceArchive? sourceArchive = null,
        WebDavConversationDetail? detailOverride = null,
        CancellationToken cancellationToken = default)
    {
        var detail = detailOverride
            ?? await new ConversationRepository(database)
                .ExportAsync(conversation.Id, cancellationToken);
        var now = _now();
        var record = new TrashRecord(
            $"{conversation.SourceAgent}-{conversation.Id}-{now.ToUnixTimeMilliseconds()}",
            conversation.SourceAgent,
            conversation.Id,
            conversation.Summary,
            now,
            now.AddDays(Math.Clamp(retentionDays, 1, 365)),
            "");
        Directory.CreateDirectory(_trashDirectory);
        var path = Path.Combine(_trashDirectory, SafeName(record.TrashId) + ".json");
        record = record with { RecordPath = path };
        var envelope = new TrashEnvelope(
            record, conversation, detail, null, sourceArchive);
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(envelope, JsonOptions),
            cancellationToken);

        try
        {
            await using var connection = database.OpenConnection();
            await using var transaction =
                await connection.BeginTransactionAsync(cancellationToken);
            var command = connection.CreateCommand();
            command.Transaction =
                (Microsoft.Data.Sqlite.SqliteTransaction)transaction;
            command.CommandText = """
                DELETE FROM messages WHERE conversation_id=$id;
                DELETE FROM conversations WHERE conversation_id=$id;
                """;
            command.Parameters.AddWithValue("$id", conversation.Id);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            if (File.Exists(path)) File.Delete(path);
            throw;
        }
        return record;
    }

    public async Task<IReadOnlyList<TrashRecord>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_trashDirectory)) return [];
        var records = new List<TrashRecord>();
        foreach (var path in Directory.EnumerateFiles(_trashDirectory, "*.json"))
        {
            try
            {
                var envelope = JsonSerializer.Deserialize<TrashEnvelope>(
                    await File.ReadAllTextAsync(path, cancellationToken),
                    JsonOptions);
                if (envelope is not null)
                {
                    var record = envelope.Record with { RecordPath = path };
                    if (record.ExpiresAt <= _now())
                    {
                        if (envelope.SourceArchive is not null)
                        {
                            await _writer.DeleteSourceArchiveAsync(
                                envelope.SourceArchive, cancellationToken);
                        }
                        File.Delete(path);
                        continue;
                    }
                    records.Add(record);
                }
            }
            catch (JsonException)
            {
                // An invalid record remains untouched and is not presented as recoverable.
            }
        }
        return records.OrderByDescending(value => value.TrashedAt).ToArray();
    }

    public async Task RestoreAsync(
        TrashRecord record,
        CancellationToken cancellationToken = default)
    {
        var envelope = JsonSerializer.Deserialize<TrashEnvelope>(
            await File.ReadAllTextAsync(record.RecordPath, cancellationToken),
            JsonOptions) ?? throw new InvalidDataException("回收站记录损坏。");

        await using var connection = database.OpenConnection();
        var exists = connection.CreateCommand();
        exists.CommandText =
            "SELECT COUNT(*) FROM conversations WHERE conversation_id=$id;";
        exists.Parameters.AddWithValue("$id", envelope.Conversation.Id);
        if (Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken)) > 0)
        {
            throw new InvalidOperationException("原位置已有同 ID 对话，拒绝覆盖。");
        }
        if (envelope.Detail is not null)
        {
            var repository = new ConversationRepository(database);
            await repository.UpsertAsync(envelope.Detail, cancellationToken);
            try
            {
                if (envelope.SourceArchive is not null)
                {
                    await _writer.RestoreSourceArchiveAsync(
                        envelope.SourceArchive, cancellationToken);
                }
            }
            catch
            {
                await repository.DeleteAsync(
                    envelope.Detail.Id, cancellationToken);
                throw;
            }
            File.Delete(record.RecordPath);
            return;
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var conversation = connection.CreateCommand();
        conversation.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)transaction;
        conversation.CommandText = """
            INSERT INTO conversations(
              conversation_id, repo_id, source_agent, source_conversation_id,
              summary, started_at, updated_at, storage_path)
            VALUES($id,$repo,$agent,$source,$summary,$started,$updated,$path);
            """;
        conversation.Parameters.AddWithValue("$id", envelope.Conversation.Id);
        conversation.Parameters.AddWithValue("$repo", envelope.Conversation.RepoId);
        conversation.Parameters.AddWithValue("$agent", envelope.Conversation.SourceAgent);
        conversation.Parameters.AddWithValue("$source", envelope.Conversation.SourceConversationId);
        conversation.Parameters.AddWithValue("$summary", envelope.Conversation.Summary);
        conversation.Parameters.AddWithValue("$started", envelope.Conversation.StartedAt.ToString("O"));
        conversation.Parameters.AddWithValue("$updated", envelope.Conversation.UpdatedAt.ToString("O"));
        conversation.Parameters.AddWithValue("$path", (object?)envelope.Conversation.StoragePath ?? DBNull.Value);
        await conversation.ExecuteNonQueryAsync(cancellationToken);

        foreach (var message in envelope.Messages ?? [])
        {
            var insert = connection.CreateCommand();
            insert.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)transaction;
            insert.CommandText = """
                INSERT INTO messages(message_id, conversation_id, role, content, timestamp)
                VALUES($id,$conversation,$role,$content,$timestamp);
                """;
            insert.Parameters.AddWithValue("$id", message.Id);
            insert.Parameters.AddWithValue("$conversation", message.ConversationId);
            insert.Parameters.AddWithValue("$role", message.Role);
            insert.Parameters.AddWithValue("$content", message.Content);
            insert.Parameters.AddWithValue("$timestamp", message.Timestamp.ToString("O"));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        File.Delete(record.RecordPath);
    }

    public async Task DeleteAsync(
        TrashRecord record,
        CancellationToken cancellationToken = default)
    {
        var envelope = ReadEnvelope(record.RecordPath);
        if (envelope?.SourceArchive is not null)
        {
            await _writer.DeleteSourceArchiveAsync(
                envelope.SourceArchive, cancellationToken);
        }
        File.Delete(record.RecordPath);
    }

    public async Task<int> EmptyAsync(
        CancellationToken cancellationToken = default)
    {
        var records = await ListAsync(cancellationToken);
        foreach (var record in records)
        {
            await DeleteAsync(record, cancellationToken);
        }
        return records.Count;
    }

    private static string SafeName(string value) =>
        string.Concat(value.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));

    private sealed record TrashEnvelope(
        TrashRecord Record,
        ConversationSummary Conversation,
        WebDavConversationDetail? Detail,
        IReadOnlyList<ConversationMessage>? Messages,
        NativeSourceArchive? SourceArchive);

    private static TrashEnvelope? ReadEnvelope(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<TrashEnvelope>(
                File.ReadAllText(path),
                JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
