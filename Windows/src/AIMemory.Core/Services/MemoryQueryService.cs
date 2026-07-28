using AIMemory.Core.Persistence;

namespace AIMemory.Core.Services;

public sealed record ProjectContextResult(
    string RepoRoot,
    IReadOnlyList<MemoryResult> ApprovedMemory,
    IReadOnlyList<CheckpointResult> RecentCheckpoints,
    IReadOnlyList<HistoryResult> RelevantHistory);

public sealed record MemoryResult(
    string Id,
    string Kind,
    string Title,
    string Value,
    string UsageHint,
    string UpdatedAt);

public sealed record CheckpointResult(
    string Id,
    string SourceAgent,
    string Summary,
    string? ResumeCommand,
    string CreatedAt);

public sealed record HistoryResult(
    string ConversationId,
    string SourceAgent,
    string Title,
    string Excerpt,
    string UpdatedAt);

public sealed class MemoryQueryService(AIMemoryDatabase database)
{
    public async Task<ProjectContextResult> GetProjectContextAsync(
        string repoRoot,
        string query,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var safeLimit = Math.Clamp(limit, 1, 20);
        await using var connection = database.OpenConnection();
        var repoId = await FindRepoIdAsync(connection, repoRoot, cancellationToken);
        if (repoId is null)
        {
            return new ProjectContextResult(repoRoot, [], [], []);
        }

        var memories = new List<MemoryResult>();
        var memory = connection.CreateCommand();
        memory.CommandText = """
            SELECT memory_id,kind,title,value,usage_hint,updated_at
            FROM approved_memories
            WHERE repo_id=$repo AND status IN ('active','approved')
            ORDER BY updated_at DESC LIMIT $limit;
            """;
        memory.Parameters.AddWithValue("$repo", repoId);
        memory.Parameters.AddWithValue("$limit", safeLimit);
        await using (var reader = await memory.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                memories.Add(new MemoryResult(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetString(4), reader.GetString(5)));
            }
        }

        var checkpoints = new List<CheckpointResult>();
        var checkpoint = connection.CreateCommand();
        checkpoint.CommandText = """
            SELECT checkpoint_id,source_agent,summary,resume_command,created_at
            FROM checkpoints WHERE repo_id=$repo AND status='active'
            ORDER BY created_at DESC LIMIT $limit;
            """;
        checkpoint.Parameters.AddWithValue("$repo", repoId);
        checkpoint.Parameters.AddWithValue("$limit", safeLimit);
        await using (var reader = await checkpoint.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                checkpoints.Add(new CheckpointResult(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetString(4)));
            }
        }
        var history = await SearchAsync(
            repoRoot, query, safeLimit, cancellationToken);
        return new ProjectContextResult(repoRoot, memories, checkpoints, history);
    }

    public async Task<IReadOnlyList<HistoryResult>> SearchAsync(
        string repoRoot,
        string query,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.OpenConnection();
        var safeLimit = Math.Clamp(limit, 1, 50);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.conversation_id,c.source_agent,COALESCE(c.summary,''),
                   substr(COALESCE(m.content,''),1,500),c.updated_at
            FROM conversations c
            JOIN repos r ON r.repo_id=c.repo_id
            LEFT JOIN messages m ON m.message_id=(
              SELECT message_id FROM messages
              WHERE conversation_id=c.conversation_id
              ORDER BY timestamp DESC,rowid DESC LIMIT 1)
            WHERE (r.repo_root=$root OR EXISTS(
              SELECT 1 FROM repo_aliases a
              WHERE a.repo_id=r.repo_id AND a.alias_root=$root))
              AND ($query='' OR c.summary LIKE '%' || $query || '%'
                   OR m.content LIKE '%' || $query || '%')
            ORDER BY c.updated_at DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$root", repoRoot);
        command.Parameters.AddWithValue("$query", query ?? "");
        command.Parameters.AddWithValue("$limit", safeLimit);
        var results = new List<HistoryResult>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new HistoryResult(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4)));
        }
        return results;
    }

    private static async Task<string?> FindRepoIdAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        string root,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT repo_id FROM repos WHERE repo_root=$root
            UNION
            SELECT repo_id FROM repo_aliases WHERE alias_root=$root
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$root", root);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }
}
