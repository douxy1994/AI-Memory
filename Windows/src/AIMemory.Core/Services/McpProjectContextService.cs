// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
using System.Text.Json;
using System.Text.Json.Serialization;
using AIMemory.Core.Persistence;
using Microsoft.Data.Sqlite;

namespace AIMemory.Core.Services;

/// <summary>
/// Source-backed projections used by the native MCP helper.  The records in
/// this file intentionally follow the macOS helper's wire contract rather
/// than the Windows view-model projections: an MCP client must be able to use
/// either helper without adapting field names or losing evidence.
/// </summary>
public sealed record McpApprovedMemory(
    string MemoryId,
    string Kind,
    string Title,
    string Value,
    string UsageHint,
    string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? LastVerifiedAt,
    string FreshnessStatus,
    double FreshnessScore);

public sealed record McpHandoffPacket(
    string HandoffId,
    string RepoRoot,
    string FromAgent,
    string ToAgent,
    string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? CheckpointId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? TargetProfile,
    string CurrentGoal,
    IReadOnlyList<string> DoneItems,
    IReadOnlyList<string> NextItems,
    IReadOnlyList<string> KeyFiles,
    IReadOnlyList<string> UsefulCommands,
    string CreatedAt);

public sealed record McpLatestScan(
    int ScannedConversationCount,
    int LinkedConversationCount,
    int SkippedConversationCount,
    IReadOnlyList<JsonElement> UnmatchedProjectRoots);

public sealed record McpRepositoryHealth(
    string RepoRoot,
    int ApprovedMemoryCount,
    int PendingCandidateCount,
    int IndexedChunkCount,
    int SearchDocumentCount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    McpLatestScan? LatestScan);

public sealed record McpHistoryConversation(
    string Id,
    string SourceAgent,
    string ProjectDir,
    string CreatedAt,
    string UpdatedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Summary,
    int MessageCount,
    int FileCount);

public sealed record McpProjectContextResult(
    string RepoRoot,
    string Query,
    string Intent,
    IReadOnlyList<McpApprovedMemory> ApprovedMemory,
    McpHandoffPacket? RecentHandoff,
    McpRepositoryHealth Health,
    IReadOnlyList<McpHistoryConversation> RelevantHistory);

public sealed class McpProjectContextService(AIMemoryDatabase database)
{
    public async Task<McpProjectContextResult> GetProjectContextAsync(
        string repoRoot,
        string query,
        string intent,
        int limit,
        CancellationToken cancellationToken = default)
    {
        Require(repoRoot, "repo_root");
        Require(query, "query");
        var safeLimit = Math.Clamp(limit, 1, 50);

        await using var connection = database.OpenConnection();
        var repoId = await FindRepoIdAsync(connection, repoRoot, cancellationToken);
        if (repoId is null)
        {
            return new McpProjectContextResult(
                repoRoot,
                query,
                intent,
                [],
                null,
                new McpRepositoryHealth(repoRoot, 0, 0, 0, 0, null),
                []);
        }

        var memories = await ReadMemoriesAsync(
            connection, repoId, safeLimit, cancellationToken);
        var handoff = await ReadRecentHandoffAsync(
            connection, repoId, cancellationToken);
        var health = await ReadHealthAsync(
            connection, repoId, repoRoot, cancellationToken);
        var history = await SearchHistoryAsync(
            connection, repoId, query, safeLimit, cancellationToken);
        return new McpProjectContextResult(
            repoRoot,
            query,
            intent,
            memories,
            handoff,
            health,
            history);
    }

    private static async Task<string?> FindRepoIdAsync(
        SqliteConnection connection,
        string repoRoot,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT repo_id FROM repos WHERE repo_root=$root
            UNION
            SELECT repo_id FROM repo_aliases WHERE alias_root=$root
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$root", repoRoot);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private static async Task<IReadOnlyList<McpApprovedMemory>> ReadMemoriesAsync(
        SqliteConnection connection,
        string repoId,
        int limit,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT memory_id,kind,title,value,usage_hint,status,
                   last_verified_at,freshness_status,freshness_score
            FROM approved_memories
            WHERE repo_id=$repo
            ORDER BY CASE status WHEN 'active' THEN 0 ELSE 1 END,
                     updated_at DESC,memory_id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$repo", repoId);
        command.Parameters.AddWithValue("$limit", limit);
        var result = new List<McpApprovedMemory>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new McpApprovedMemory(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetString(7),
                reader.GetDouble(8)));
        }
        return result;
    }

    private static async Task<McpHandoffPacket?> ReadRecentHandoffAsync(
        SqliteConnection connection,
        string repoId,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT hp.handoff_id,r.repo_root,hp.from_agent,hp.to_agent,
                   hp.status,hp.checkpoint_id,hp.target_profile,
                   hp.current_goal,hp.done_json,hp.next_json,
                   hp.key_files_json,hp.commands_json,hp.created_at
            FROM handoff_packets hp
            JOIN repos r ON r.repo_id=hp.repo_id
            WHERE hp.repo_id=$repo
            ORDER BY hp.created_at DESC,hp.handoff_id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$repo", repoId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new McpHandoffPacket(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetString(7),
            ParseStringArray(reader.GetString(8)),
            ParseStringArray(reader.GetString(9)),
            ParseStringArray(reader.GetString(10)),
            ParseStringArray(reader.GetString(11)),
            reader.GetString(12));
    }

    private static async Task<McpRepositoryHealth> ReadHealthAsync(
        SqliteConnection connection,
        string repoId,
        string requestedRepoRoot,
        CancellationToken cancellationToken)
    {
        var count = connection.CreateCommand();
        count.CommandText = """
            SELECT
              (SELECT COUNT(*) FROM approved_memories
                 WHERE repo_id=$repo AND status='active'),
              (SELECT COUNT(*) FROM memory_candidates
                 WHERE repo_id=$repo AND status IN ('pending','pending_review')),
              (SELECT COUNT(*) FROM conversation_chunks WHERE repo_id=$repo),
              (SELECT COUNT(*) FROM search_documents WHERE repo_id=$repo);
            """;
        count.Parameters.AddWithValue("$repo", repoId);
        await using var countReader = await count.ExecuteReaderAsync(cancellationToken);
        if (!await countReader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Unable to read repository health.");
        }

        var health = new McpRepositoryHealth(
            requestedRepoRoot,
            countReader.GetInt32(0),
            countReader.GetInt32(1),
            countReader.GetInt32(2),
            countReader.GetInt32(3),
            null);
        await countReader.CloseAsync();

        var scan = connection.CreateCommand();
        scan.CommandText = """
            SELECT scanned_conversation_count,linked_conversation_count,
                   skipped_conversation_count,unmatched_project_roots_json
            FROM repo_scan_runs
            WHERE repo_id=$repo
            ORDER BY scanned_at DESC
            LIMIT 1;
            """;
        scan.Parameters.AddWithValue("$repo", repoId);
        await using var scanReader = await scan.ExecuteReaderAsync(cancellationToken);
        if (!await scanReader.ReadAsync(cancellationToken)) return health;
        return health with
        {
            LatestScan = new McpLatestScan(
                scanReader.GetInt32(0),
                scanReader.GetInt32(1),
                scanReader.GetInt32(2),
                ParseJsonArray(scanReader.GetString(3))),
        };
    }

    private static async Task<IReadOnlyList<McpHistoryConversation>> SearchHistoryAsync(
        SqliteConnection connection,
        string repoId,
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.source_conversation_id,c.source_agent,
                   COALESCE(r.repo_root,''),c.started_at,c.updated_at,
                   c.summary,
                   (SELECT COUNT(*) FROM messages m
                     WHERE m.conversation_id=c.conversation_id),
                   (SELECT COUNT(*) FROM file_changes f
                     WHERE f.conversation_id=c.conversation_id)
            FROM conversations c
            LEFT JOIN repos r ON r.repo_id=c.repo_id
            WHERE c.repo_id=$repo
              AND (
                COALESCE(c.summary,'') LIKE $pattern ESCAPE '\'
                OR EXISTS(
                  SELECT 1 FROM messages m
                  WHERE m.conversation_id=c.conversation_id
                    AND m.content LIKE $pattern ESCAPE '\'
                )
              )
            ORDER BY c.updated_at DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$repo", repoId);
        command.Parameters.AddWithValue("$pattern", $"%{EscapeLike(query)}%");
        command.Parameters.AddWithValue("$limit", limit);
        var result = new List<McpHistoryConversation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new McpHistoryConversation(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetInt32(6),
                reader.GetInt32(7)));
        }
        return result;
    }

    private static IReadOnlyList<string> ParseStringArray(string value)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(value) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IReadOnlyList<JsonElement> ParseJsonArray(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return [];
            return document.RootElement.EnumerateArray()
                .Select(element => element.Clone())
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string EscapeLike(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    private static void Require(string value, string key)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Missing required argument: {key}", key);
        }
    }
}
