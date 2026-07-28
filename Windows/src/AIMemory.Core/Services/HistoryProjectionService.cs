using AIMemory.Core.Persistence;

namespace AIMemory.Core.Services;

public sealed record AgentRunRecord(
    string Id,
    string RepoId,
    string SourceAgent,
    string? TaskHint,
    string Status,
    string Summary,
    string StartedAt,
    string? EndedAt)
{
    public string DisplayTitle =>
        string.IsNullOrWhiteSpace(TaskHint) ? Summary : TaskHint;
}

public sealed record ArtifactRecord(
    string Id,
    string RunId,
    string Type,
    string Title,
    string Summary,
    string? Body,
    string? FilePath,
    string TrustState,
    string CreatedAt);

public sealed record EpisodeRecord(
    string Id,
    string RepoId,
    string Title,
    string Summary,
    string Outcome,
    string CreatedAt,
    string SourceConversationId);

public sealed record WikiRecord(
    string Id,
    string RepoId,
    string Slug,
    string Title,
    string Body,
    string Status,
    string UpdatedAt);

public sealed class HistoryProjectionService(AIMemoryDatabase database)
{
    public async Task<IReadOnlyList<AgentRunRecord>> ListRunsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.OpenConnection();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT run_id,repo_id,source_agent,task_hint,status,summary,
                   started_at,ended_at
            FROM agent_runs ORDER BY started_at DESC;
            """;
        var result = new List<AgentRunRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new AgentRunRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }
        return result;
    }

    public async Task<IReadOnlyList<ArtifactRecord>> ListArtifactsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.OpenConnection();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT artifact_id,run_id,artifact_type,title,summary,body,
                   file_path,trust_state,created_at
            FROM artifacts ORDER BY created_at DESC;
            """;
        var result = new List<ArtifactRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ArtifactRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8)));
        }
        return result;
    }

    public async Task<IReadOnlyList<EpisodeRecord>> ListEpisodesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.OpenConnection();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT episode_id,repo_id,title,summary,outcome,created_at,
                   source_conversation_id
            FROM episodes ORDER BY created_at DESC;
            """;
        var result = new List<EpisodeRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new EpisodeRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6)));
        }
        return result;
    }

    public async Task<IReadOnlyList<WikiRecord>> ListWikiAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.OpenConnection();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT page_id,repo_id,slug,title,body,status,updated_at
            FROM wiki_pages ORDER BY updated_at DESC;
            """;
        var result = new List<WikiRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new WikiRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6)));
        }
        return result;
    }

    public static string ConversationIdForRun(string runId) =>
        runId.StartsWith("run:", StringComparison.OrdinalIgnoreCase)
            ? runId[4..]
            : runId;
}
