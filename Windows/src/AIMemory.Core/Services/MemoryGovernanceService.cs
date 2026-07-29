using AIMemory.Core.Persistence;

namespace AIMemory.Core.Services;

public sealed record MemoryCandidateRecord(
    string Id,
    string RepoId,
    string Kind,
    string Summary,
    string Value,
    string WhyItMatters,
    double Confidence,
    string Status,
    string CreatedAt);

public sealed record ApprovedMemoryRecord(
    string Id,
    string RepoId,
    string Kind,
    string Title,
    string Value,
    string UsageHint,
    string Status,
    string FreshnessStatus,
    double FreshnessScore,
    string UpdatedAt);

public sealed class MemoryGovernanceService(AIMemoryDatabase database)
{
    public async Task<IReadOnlyList<MemoryCandidateRecord>> ListCandidatesAsync(
        bool includeReviewed = false,
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.OpenConnection();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT candidate_id,repo_id,kind,summary,value,why_it_matters,
                   confidence,status,created_at
            FROM memory_candidates
            WHERE $all=1 OR status IN ('pending','pending_review')
            ORDER BY created_at DESC;
            """;
        command.Parameters.AddWithValue("$all", includeReviewed ? 1 : 0);
        var result = new List<MemoryCandidateRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new MemoryCandidateRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetDouble(6),
                reader.GetString(7),
                reader.GetString(8)));
        }
        return result;
    }

    public async Task<IReadOnlyList<ApprovedMemoryRecord>> ListApprovedAsync(
        bool includeRetired = false,
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.OpenConnection();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT memory_id,repo_id,kind,title,value,usage_hint,status,
                   freshness_status,freshness_score,updated_at
            FROM approved_memories
            WHERE $all=1 OR status IN ('active','approved')
            ORDER BY updated_at DESC;
            """;
        command.Parameters.AddWithValue("$all", includeRetired ? 1 : 0);
        var result = new List<ApprovedMemoryRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ApprovedMemoryRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetDouble(8),
                reader.GetString(9)));
        }
        return result;
    }

    public async Task ApproveCandidateAsync(
        string candidateId,
        string title,
        string value,
        string usageHint,
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.OpenConnection();
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var read = connection.CreateCommand();
        read.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)transaction;
        read.CommandText = """
            SELECT repo_id,kind,summary,value FROM memory_candidates
            WHERE candidate_id=$id AND status IN ('pending','pending_review')
            LIMIT 1;
            """;
        read.Parameters.AddWithValue("$id", candidateId);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new KeyNotFoundException($"找不到待审候选 {candidateId}。");
        }
        var repoId = reader.GetString(0);
        var kind = reader.GetString(1);
        var fallbackTitle = reader.GetString(2);
        var fallbackValue = reader.GetString(3);
        await reader.CloseAsync();

        var now = DateTimeOffset.UtcNow.ToString("O");
        var memoryId = Guid.NewGuid().ToString();
        var insert = connection.CreateCommand();
        insert.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)transaction;
        insert.CommandText = """
            INSERT INTO approved_memories(
              memory_id,repo_id,kind,title,value,usage_hint,status,
              last_verified_at,created_from_candidate_id,created_at,updated_at,
              freshness_status,freshness_score,verified_at,verified_by)
            VALUES($memory,$repo,$kind,$title,$value,$hint,'active',
                   $now,$candidate,$now,$now,'fresh',1.0,$now,'user');
            UPDATE memory_candidates SET status='approved',reviewed_at=$now
            WHERE candidate_id=$candidate;
            """;
        insert.Parameters.AddWithValue("$memory", memoryId);
        insert.Parameters.AddWithValue("$repo", repoId);
        insert.Parameters.AddWithValue("$kind", kind);
        insert.Parameters.AddWithValue(
            "$title",
            string.IsNullOrWhiteSpace(title) ? fallbackTitle : title.Trim());
        insert.Parameters.AddWithValue(
            "$value",
            string.IsNullOrWhiteSpace(value) ? fallbackValue : value.Trim());
        insert.Parameters.AddWithValue("$hint", usageHint.Trim());
        insert.Parameters.AddWithValue("$candidate", candidateId);
        insert.Parameters.AddWithValue("$now", now);
        await insert.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task ReviewCandidateAsync(
        string candidateId,
        string action,
        CancellationToken cancellationToken = default)
    {
        var status = action switch
        {
            "reject" => "rejected",
            "snooze" => "snoozed",
            _ => throw new ArgumentOutOfRangeException(
                nameof(action), "仅支持 reject 或 snooze。"),
        };
        await using var connection = database.OpenConnection();
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE memory_candidates SET status=$status,reviewed_at=$now
            WHERE candidate_id=$id AND status IN ('pending','pending_review');
            """;
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", candidateId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            throw new KeyNotFoundException($"找不到待审候选 {candidateId}。");
        }
    }

    public async Task<int> ReviewAllPendingAsync(
        string action,
        string? repoId = null,
        CancellationToken cancellationToken = default)
    {
        var status = action switch
        {
            "reject" => "rejected",
            "snooze" => "snoozed",
            _ => throw new ArgumentOutOfRangeException(
                nameof(action), "仅支持 reject 或 snooze。"),
        };
        await using var connection = database.OpenConnection();
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE memory_candidates
            SET status=$status,reviewed_at=$now
            WHERE status IN ('pending','pending_review')
              AND ($repo IS NULL OR repo_id=$repo);
            """;
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue(
            "$now",
            DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue(
            "$repo",
            string.IsNullOrWhiteSpace(repoId)
                ? DBNull.Value
                : repoId.Trim());
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateApprovedAsync(
        string memoryId,
        string title,
        string value,
        string usageHint,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("标题和值不能为空。");
        }
        await using var connection = database.OpenConnection();
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE approved_memories
            SET title=$title,value=$value,usage_hint=$hint,updated_at=$now
            WHERE memory_id=$id;
            """;
        command.Parameters.AddWithValue("$title", title.Trim());
        command.Parameters.AddWithValue("$value", value.Trim());
        command.Parameters.AddWithValue("$hint", usageHint.Trim());
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", memoryId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            throw new KeyNotFoundException($"找不到记忆 {memoryId}。");
        }
    }

    public async Task SetApprovedStateAsync(
        string memoryId,
        bool active,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        await using var connection = database.OpenConnection();
        var command = connection.CreateCommand();
        command.CommandText = active
            ? """
              UPDATE approved_memories
              SET status='active',last_verified_at=$now,verified_at=$now,
                  verified_by='user',freshness_status='fresh',
                  freshness_score=1.0,updated_at=$now
              WHERE memory_id=$id;
              """
            : """
              UPDATE approved_memories
              SET status='retired',updated_at=$now WHERE memory_id=$id;
              """;
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$id", memoryId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            throw new KeyNotFoundException($"找不到记忆 {memoryId}。");
        }
    }
}
