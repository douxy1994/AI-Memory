using System.Security.Cryptography;
using System.Text;
using AIMemory.Core.Persistence;

namespace AIMemory.Core.Services;

public sealed record RepoAliasResult(
    string RepoId,
    string RepoRoot,
    string AliasRoot);

public sealed record MemoryMergeProposalRecord(
    string Id,
    string RepoId,
    string CandidateId,
    string TargetMemoryId,
    string Title,
    string Value,
    string UsageHint,
    string RiskNote,
    string ProposedBy,
    string Status,
    string CreatedAt,
    string UpdatedAt);

public sealed class RepositoryGovernanceService(AIMemoryDatabase database)
{
    public async Task<string?> ResolveRepoIdAsync(
        string repoRoot,
        bool create = false,
        CancellationToken cancellationToken = default)
    {
        var root = Required(repoRoot, nameof(repoRoot));
        await using var connection = database.OpenConnection();
        var find = connection.CreateCommand();
        find.CommandText = """
            SELECT repo_id FROM repos WHERE repo_root=$root
            UNION
            SELECT repo_id FROM repo_aliases WHERE alias_root=$root
            LIMIT 1;
            """;
        find.Parameters.AddWithValue("$root", root);
        if (await find.ExecuteScalarAsync(cancellationToken) is string found)
        {
            return found;
        }
        if (!create) return null;

        var id = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(root)))
            .ToLowerInvariant();
        var now = DateTimeOffset.UtcNow.ToString("O");
        var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO repos(
              repo_id,repo_root,repo_fingerprint,git_remote,default_branch,
              created_at,updated_at)
            VALUES($id,$root,$fingerprint,NULL,NULL,$now,$now);
            """;
        insert.Parameters.AddWithValue("$id", id);
        insert.Parameters.AddWithValue("$root", root);
        insert.Parameters.AddWithValue("$fingerprint", id);
        insert.Parameters.AddWithValue("$now", now);
        await insert.ExecuteNonQueryAsync(cancellationToken);
        return id;
    }

    public async Task<RepoAliasResult> MergeAliasAsync(
        string repoRoot,
        string aliasRoot,
        CancellationToken cancellationToken = default)
    {
        var root = Required(repoRoot, nameof(repoRoot));
        var alias = Required(aliasRoot, nameof(aliasRoot));
        if (string.Equals(root, alias, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("仓库路径与别名路径不能相同。");
        }
        var repoId = await ResolveRepoIdAsync(
            root, create: true, cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("无法创建仓库记录。");
        await using var connection = database.OpenConnection();
        var conflict = connection.CreateCommand();
        conflict.CommandText = """
            SELECT repo_id FROM repos WHERE repo_root=$alias
            UNION
            SELECT repo_id FROM repo_aliases WHERE alias_root=$alias
            LIMIT 1;
            """;
        conflict.Parameters.AddWithValue("$alias", alias);
        if (await conflict.ExecuteScalarAsync(cancellationToken) is string owner
            && owner != repoId)
        {
            throw new InvalidOperationException(
                "该别名已属于另一个仓库，拒绝自动合并。");
        }

        var now = DateTimeOffset.UtcNow.ToString("O");
        var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO repo_aliases(
              alias_id,repo_id,alias_root,alias_kind,confidence,
              created_at,updated_at)
            VALUES($id,$repo,$alias,'user',1.0,$now,$now)
            ON CONFLICT(repo_id,alias_root) DO UPDATE SET
              alias_kind='user',confidence=1.0,updated_at=excluded.updated_at;
            """;
        insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
        insert.Parameters.AddWithValue("$repo", repoId);
        insert.Parameters.AddWithValue("$alias", alias);
        insert.Parameters.AddWithValue("$now", now);
        await insert.ExecuteNonQueryAsync(cancellationToken);
        return new RepoAliasResult(repoId, root, alias);
    }

    public async Task<string> CreateMemoryCandidateAsync(
        string repoRoot,
        string kind,
        string summary,
        string value,
        string whyItMatters,
        double confidence,
        string proposedBy,
        CancellationToken cancellationToken = default)
    {
        var repoId = await ResolveRepoIdAsync(
            repoRoot, create: true, cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("无法创建仓库记录。");
        var id = Guid.NewGuid().ToString();
        var now = DateTimeOffset.UtcNow.ToString("O");
        await using var connection = database.OpenConnection();
        var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO memory_candidates(
              candidate_id,repo_id,kind,summary,value,why_it_matters,
              confidence,proposed_by,status,created_at,reviewed_at)
            VALUES($id,$repo,$kind,$summary,$value,$why,$confidence,
                   $proposed,'pending_review',$now,NULL);
            """;
        insert.Parameters.AddWithValue("$id", id);
        insert.Parameters.AddWithValue("$repo", repoId);
        insert.Parameters.AddWithValue("$kind", Required(kind, nameof(kind)));
        insert.Parameters.AddWithValue(
            "$summary", Required(summary, nameof(summary)));
        insert.Parameters.AddWithValue("$value", Required(value, nameof(value)));
        insert.Parameters.AddWithValue("$why", whyItMatters.Trim());
        insert.Parameters.AddWithValue(
            "$confidence", Math.Clamp(confidence, 0, 1));
        insert.Parameters.AddWithValue(
            "$proposed",
            string.IsNullOrWhiteSpace(proposedBy) ? "mcp" : proposedBy.Trim());
        insert.Parameters.AddWithValue("$now", now);
        await insert.ExecuteNonQueryAsync(cancellationToken);
        return id;
    }

    public async Task<IReadOnlyList<MemoryCandidateRecord>> ListCandidatesAsync(
        string repoRoot,
        string? status = null,
        CancellationToken cancellationToken = default)
    {
        var repoId = await ResolveRepoIdAsync(
            repoRoot, cancellationToken: cancellationToken);
        if (repoId is null) return [];
        var all = await new MemoryGovernanceService(database)
            .ListCandidatesAsync(
                includeReviewed: true,
                cancellationToken: cancellationToken);
        return all.Where(value =>
                value.RepoId == repoId
                && (string.IsNullOrWhiteSpace(status)
                    || value.Status.Equals(
                        status,
                        StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

    public async Task<MemoryMergeProposalRecord> ProposeMemoryMergeAsync(
        string repoRoot,
        string candidateId,
        string targetMemoryId,
        string title,
        string value,
        string usageHint,
        string riskNote,
        string proposedBy,
        CancellationToken cancellationToken = default)
    {
        var repoId = await ResolveRepoIdAsync(
            repoRoot, cancellationToken: cancellationToken)
            ?? throw new KeyNotFoundException("找不到仓库。");
        await using var connection = database.OpenConnection();
        var verify = connection.CreateCommand();
        verify.CommandText = """
            SELECT
              EXISTS(SELECT 1 FROM memory_candidates
                     WHERE candidate_id=$candidate AND repo_id=$repo),
              EXISTS(SELECT 1 FROM approved_memories
                     WHERE memory_id=$memory AND repo_id=$repo);
            """;
        verify.Parameters.AddWithValue("$candidate", candidateId);
        verify.Parameters.AddWithValue("$memory", targetMemoryId);
        verify.Parameters.AddWithValue("$repo", repoId);
        await using (var reader = await verify.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)
                || reader.GetInt32(0) == 0
                || reader.GetInt32(1) == 0)
            {
                throw new KeyNotFoundException(
                    "候选记忆或目标记忆不属于当前仓库。");
            }
        }

        var now = DateTimeOffset.UtcNow.ToString("O");
        var existing = connection.CreateCommand();
        existing.CommandText = """
            SELECT proposal_id,created_at FROM memory_merge_proposals
            WHERE candidate_id=$candidate AND target_memory_id=$memory
            LIMIT 1;
            """;
        existing.Parameters.AddWithValue("$candidate", candidateId);
        existing.Parameters.AddWithValue("$memory", targetMemoryId);
        string id = Guid.NewGuid().ToString();
        string createdAt = now;
        await using (var reader = await existing.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                id = reader.GetString(0);
                createdAt = reader.GetString(1);
            }
        }

        var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO memory_merge_proposals(
              proposal_id,repo_id,candidate_id,target_memory_id,
              proposed_title,proposed_value,proposed_usage_hint,risk_note,
              proposed_by,status,created_at,updated_at)
            VALUES($id,$repo,$candidate,$memory,$title,$value,$hint,$risk,
                   $by,'pending_review',$created,$updated)
            ON CONFLICT(candidate_id,target_memory_id) DO UPDATE SET
              proposed_title=excluded.proposed_title,
              proposed_value=excluded.proposed_value,
              proposed_usage_hint=excluded.proposed_usage_hint,
              risk_note=excluded.risk_note,
              proposed_by=excluded.proposed_by,
              status='pending_review',
              updated_at=excluded.updated_at;
            """;
        insert.Parameters.AddWithValue("$id", id);
        insert.Parameters.AddWithValue("$repo", repoId);
        insert.Parameters.AddWithValue("$candidate", candidateId);
        insert.Parameters.AddWithValue("$memory", targetMemoryId);
        insert.Parameters.AddWithValue("$title", Required(title, nameof(title)));
        insert.Parameters.AddWithValue("$value", Required(value, nameof(value)));
        insert.Parameters.AddWithValue("$hint", usageHint.Trim());
        insert.Parameters.AddWithValue("$risk", riskNote.Trim());
        insert.Parameters.AddWithValue(
            "$by",
            string.IsNullOrWhiteSpace(proposedBy) ? "mcp" : proposedBy.Trim());
        insert.Parameters.AddWithValue("$created", createdAt);
        insert.Parameters.AddWithValue("$updated", now);
        await insert.ExecuteNonQueryAsync(cancellationToken);
        return new MemoryMergeProposalRecord(
            id, repoId, candidateId, targetMemoryId,
            title.Trim(), value.Trim(), usageHint.Trim(), riskNote.Trim(),
            string.IsNullOrWhiteSpace(proposedBy) ? "mcp" : proposedBy.Trim(),
            "pending_review", createdAt, now);
    }

    private static string Required(string value, string parameter) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"{parameter} 不能为空。", parameter)
            : value.Trim();
}
