using System.Text.Json;
using AIMemory.Core.Models;
using AIMemory.Core.Persistence;

namespace AIMemory.Core.Services;

public sealed record CheckpointRecord(
    string Id,
    string RepoId,
    string ConversationId,
    string SourceAgent,
    string Status,
    string Summary,
    string? ResumeCommand,
    string MetadataJson,
    string? HandoffId,
    string CreatedAt);

public sealed record HandoffRecord(
    string Id,
    string RepoId,
    string FromAgent,
    string ToAgent,
    string CurrentGoal,
    string DoneJson,
    string NextJson,
    string KeyFilesJson,
    string CommandsJson,
    string Status,
    string? TargetProfile,
    string? CheckpointId,
    string CreatedAt);

public sealed class RecoveryService(AIMemoryDatabase database)
{
    public async Task<IReadOnlyList<CheckpointRecord>> ListCheckpointsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.OpenConnection();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT checkpoint_id,repo_id,conversation_id,source_agent,status,
                   summary,resume_command,metadata_json,handoff_id,created_at
            FROM checkpoints ORDER BY created_at DESC;
            """;
        var result = new List<CheckpointRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new CheckpointRecord(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.GetString(9)));
        }
        return result;
    }

    public async Task<IReadOnlyList<HandoffRecord>> ListHandoffsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.OpenConnection();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT handoff_id,repo_id,from_agent,to_agent,current_goal,
                   done_json,next_json,key_files_json,commands_json,status,
                   target_profile,checkpoint_id,created_at
            FROM handoff_packets ORDER BY created_at DESC;
            """;
        var result = new List<HandoffRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new HandoffRecord(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5),
                reader.GetString(6), reader.GetString(7), reader.GetString(8),
                reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.GetString(12)));
        }
        return result;
    }

    public async Task<CheckpointRecord> CreateCheckpointAsync(
        ConversationSummary conversation,
        int messageCount,
        CancellationToken cancellationToken = default)
        => await CreateCheckpointAsync(
            conversation,
            messageCount,
            summary: null,
            resumeCommand: null,
            metadataJson: null,
            cancellationToken: cancellationToken);

    public async Task<CheckpointRecord> CreateCheckpointAsync(
        ConversationSummary conversation,
        int messageCount,
        string? summary,
        string? resumeCommand,
        string? metadataJson,
        CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid().ToString();
        var now = DateTimeOffset.UtcNow.ToString("O");
        var checkpointSummary = string.IsNullOrWhiteSpace(summary)
            ? (string.IsNullOrWhiteSpace(conversation.Summary)
                ? conversation.Id
                : conversation.Summary)
            : summary.Trim();
        var resume = string.IsNullOrWhiteSpace(resumeCommand)
            ? ResumeCommand(conversation.SourceAgent, conversation.Id)
            : resumeCommand.Trim();
        var metadata = string.IsNullOrWhiteSpace(metadataJson)
            ? JsonSerializer.Serialize(new
            {
                message_count = messageCount,
                capture = "manual",
            })
            : ValidateJsonObject(metadataJson);
        await using var connection = database.OpenConnection();
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO checkpoints(
              checkpoint_id,repo_id,conversation_id,source_agent,status,
              summary,resume_command,metadata_json,handoff_id,created_at)
            VALUES($id,$repo,$conversation,$agent,'active',$summary,$resume,
                   $metadata,NULL,$now);
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$repo", conversation.RepoId);
        command.Parameters.AddWithValue("$conversation", conversation.Id);
        command.Parameters.AddWithValue("$agent", conversation.SourceAgent);
        command.Parameters.AddWithValue("$summary", checkpointSummary);
        command.Parameters.AddWithValue("$resume", (object?)resume ?? DBNull.Value);
        command.Parameters.AddWithValue("$metadata", metadata);
        command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return new CheckpointRecord(
            id, conversation.RepoId, conversation.Id, conversation.SourceAgent,
            "active",
            checkpointSummary,
            resume,
            metadata,
            null,
            now);
    }

    public async Task<CheckpointRecord> UpsertAutomaticCheckpointAsync(
        ConversationSummary conversation,
        string checkpointConversationId,
        string summary,
        string? resumeCommand,
        string metadataJson,
        CancellationToken cancellationToken = default)
    {
        using var metadataDocument = JsonDocument.Parse(metadataJson);
        if (metadataDocument.RootElement.ValueKind != JsonValueKind.Object
            || !metadataDocument.RootElement.TryGetProperty(
                "capture",
                out var capture)
            || capture.ValueKind != JsonValueKind.String
            || capture.GetString() != "auto")
        {
            throw new ArgumentException(
                "自动恢复点 metadata 必须包含 capture=auto。",
                nameof(metadataJson));
        }

        var now = DateTimeOffset.UtcNow.ToString("O");
        var checkpointSummary = string.IsNullOrWhiteSpace(summary)
            ? conversation.Id
            : summary.Trim();
        var resume = string.IsNullOrWhiteSpace(resumeCommand)
            ? ResumeCommand(conversation.SourceAgent, conversation.Id)
            : resumeCommand.Trim();

        await using var connection = database.OpenConnection();
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        var find = connection.CreateCommand();
        find.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)transaction;
        find.CommandText = """
            SELECT checkpoint_id,metadata_json
            FROM checkpoints
            WHERE repo_id=$repo
              AND conversation_id=$conversation
              AND lower(source_agent)=lower($agent)
              AND status='active'
              AND handoff_id IS NULL
            ORDER BY created_at DESC;
            """;
        find.Parameters.AddWithValue("$repo", conversation.RepoId);
        find.Parameters.AddWithValue(
            "$conversation",
            checkpointConversationId);
        find.Parameters.AddWithValue("$agent", conversation.SourceAgent);
        string? existingId = null;
        await using (var reader =
                     await find.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                try
                {
                    using var existingMetadata = JsonDocument.Parse(
                        reader.GetString(1));
                    if (existingMetadata.RootElement.TryGetProperty(
                            "capture",
                            out var existingCapture)
                        && existingCapture.ValueKind
                            == JsonValueKind.String
                        && existingCapture.GetString() == "auto")
                    {
                        existingId = reader.GetString(0);
                        break;
                    }
                }
                catch (JsonException)
                {
                    // A malformed manual checkpoint must not block capture.
                }
            }
        }

        var checkpointId = existingId ?? Guid.NewGuid().ToString();
        var command = connection.CreateCommand();
        command.Transaction =
            (Microsoft.Data.Sqlite.SqliteTransaction)transaction;
        if (existingId is null)
        {
            command.CommandText = """
                INSERT INTO checkpoints(
                  checkpoint_id,repo_id,conversation_id,source_agent,status,
                  summary,resume_command,metadata_json,handoff_id,created_at)
                VALUES($id,$repo,$conversation,$agent,'active',$summary,
                       $resume,$metadata,NULL,$now);
                """;
            command.Parameters.AddWithValue(
                "$repo",
                conversation.RepoId);
            command.Parameters.AddWithValue(
                "$conversation",
                checkpointConversationId);
            command.Parameters.AddWithValue(
                "$agent",
                conversation.SourceAgent);
        }
        else
        {
            command.CommandText = """
                UPDATE checkpoints
                SET summary=$summary,resume_command=$resume,
                    metadata_json=$metadata,created_at=$now
                WHERE checkpoint_id=$id;
                """;
        }
        command.Parameters.AddWithValue("$id", checkpointId);
        command.Parameters.AddWithValue("$summary", checkpointSummary);
        command.Parameters.AddWithValue(
            "$resume",
            (object?)resume ?? DBNull.Value);
        command.Parameters.AddWithValue("$metadata", metadataJson);
        command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new CheckpointRecord(
            checkpointId,
            conversation.RepoId,
            checkpointConversationId,
            conversation.SourceAgent,
            "active",
            checkpointSummary,
            resume,
            metadataJson,
            null,
            now);
    }

    public async Task<HandoffRecord> CreateHandoffAsync(
        CheckpointRecord checkpoint,
        string toAgent,
        string? targetProfile = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(toAgent))
        {
            throw new ArgumentException("目标 Agent 不能为空。", nameof(toAgent));
        }
        var id = Guid.NewGuid().ToString();
        var now = DateTimeOffset.UtcNow.ToString("O");
        await using var connection = database.OpenConnection();
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var keyFiles = connection.CreateCommand();
        keyFiles.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)transaction;
        keyFiles.CommandText = """
            SELECT DISTINCT path FROM file_changes
            WHERE conversation_id=$conversation
            ORDER BY timestamp DESC LIMIT 12;
            """;
        keyFiles.Parameters.AddWithValue("$conversation", checkpoint.ConversationId);
        var paths = new List<string>();
        await using (var reader = await keyFiles.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken)) paths.Add(reader.GetString(0));
        }

        var commands = connection.CreateCommand();
        commands.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)transaction;
        commands.CommandText = """
            SELECT value FROM approved_memories
            WHERE repo_id=$repo AND status IN ('active','approved') AND kind='command'
            ORDER BY updated_at DESC LIMIT 10;
            """;
        commands.Parameters.AddWithValue("$repo", checkpoint.RepoId);
        var commandValues = new List<string>();
        await using (var reader = await commands.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                commandValues.Add(reader.GetString(0));
            }
        }

        var insert = connection.CreateCommand();
        insert.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)transaction;
        insert.CommandText = """
            INSERT INTO handoff_packets(
              handoff_id,repo_id,from_agent,to_agent,current_goal,done_json,
              next_json,key_files_json,commands_json,related_memories_json,
              related_episodes_json,created_at,status,target_profile,
              checkpoint_id,compression_strategy,consumed_at,consumed_by)
            VALUES($id,$repo,$from,$to,$goal,'[]','[]',$files,$commands,
                   '[]','[]',$now,'draft',$profile,$checkpoint,
                   'source-backed',NULL,NULL);
            UPDATE checkpoints SET handoff_id=$id WHERE checkpoint_id=$checkpoint;
            """;
        insert.Parameters.AddWithValue("$id", id);
        insert.Parameters.AddWithValue("$repo", checkpoint.RepoId);
        insert.Parameters.AddWithValue("$from", checkpoint.SourceAgent);
        insert.Parameters.AddWithValue("$to", toAgent.Trim().ToLowerInvariant());
        insert.Parameters.AddWithValue("$goal", checkpoint.Summary);
        insert.Parameters.AddWithValue("$files", JsonSerializer.Serialize(paths));
        insert.Parameters.AddWithValue(
            "$commands", JsonSerializer.Serialize(commandValues));
        insert.Parameters.AddWithValue("$now", now);
        insert.Parameters.AddWithValue(
            "$profile",
            string.IsNullOrWhiteSpace(targetProfile)
                ? DBNull.Value
                : targetProfile.Trim());
        insert.Parameters.AddWithValue("$checkpoint", checkpoint.Id);
        await insert.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new HandoffRecord(
            id, checkpoint.RepoId, checkpoint.SourceAgent,
            toAgent.Trim().ToLowerInvariant(), checkpoint.Summary,
            "[]", "[]", JsonSerializer.Serialize(paths),
            JsonSerializer.Serialize(commandValues), "draft",
            targetProfile, checkpoint.Id, now);
    }

    public async Task MarkHandoffConsumedAsync(
        string handoffId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.OpenConnection();
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE handoff_packets
            SET status='consumed',consumed_at=$now,consumed_by='user'
            WHERE handoff_id=$id;
            """;
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", handoffId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            throw new KeyNotFoundException($"找不到交接包 {handoffId}。");
        }
    }

    private static string? ResumeCommand(string agent, string conversationId) =>
        agent.ToLowerInvariant() switch
        {
            "claude" => $"claude --resume {conversationId}",
            "codex" => $"codex resume {conversationId}",
            "gemini" => $"gemini --resume {conversationId}",
            "kimi" => $"kimi --resume {conversationId}",
            _ => null,
        };

    private static string ValidateJsonObject(string value)
    {
        using var document = JsonDocument.Parse(value);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("metadata_json 必须是 JSON 对象。");
        }
        return document.RootElement.GetRawText();
    }
}
