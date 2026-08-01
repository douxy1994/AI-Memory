// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
using AIMemory.Core.Models;
using AIMemory.Core.Persistence;

namespace AIMemory.Core.Services;

public sealed record WorkbenchConversationInsight(
    ConversationSummary Conversation,
    int MessageCount,
    int FileCount,
    bool IsFavorite)
{
    public int SignalScore => FileCount * 5 + MessageCount;
}

public sealed record WorkbenchInsights(
    int FavoriteCount,
    int ApprovedMemoryCount,
    int PendingCandidateCount,
    int WikiPageCount,
    string RecommendedAgent,
    bool RecommendationUsesFileChanges,
    IReadOnlyList<WorkbenchConversationInsight> HighSignalConversations,
    IReadOnlyList<WorkbenchConversationInsight> CleanupCandidates);

public sealed class WorkbenchInsightService(AIMemoryDatabase database)
{
    public async Task<WorkbenchInsights> LoadAsync(
        IReadOnlyDictionary<string, FavoriteConversationSnapshot> favorites,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default,
        IReadOnlySet<string>? availableAgentIds = null)
    {
        var favoriteKeys = favorites.Keys.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        var favoriteIds = favorites.Values
            .Select(value => value.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        await using var connection = database.OpenConnection();
        var command = connection.CreateCommand();
        command.CommandText = """
            WITH recent_conversations AS (
              SELECT c.conversation_id,c.repo_id,c.source_agent,
                     c.source_conversation_id,COALESCE(c.summary,'') AS summary,
                     c.started_at,c.updated_at,c.storage_path,
                     COALESCE(r.repo_root,'') AS repo_root
              FROM conversations c
              LEFT JOIN repos r ON r.repo_id=c.repo_id
              ORDER BY c.updated_at DESC
              LIMIT 5000
            ), message_counts AS (
              SELECT conversation_id,COUNT(*) AS count
              FROM messages
              WHERE conversation_id IN (
                SELECT conversation_id FROM recent_conversations)
              GROUP BY conversation_id
            ), file_counts AS (
              SELECT conversation_id,COUNT(*) AS count
              FROM file_changes
              WHERE conversation_id IN (
                SELECT conversation_id FROM recent_conversations)
              GROUP BY conversation_id
            )
            SELECT c.conversation_id,c.repo_id,c.source_agent,
                   c.source_conversation_id,c.summary,
                   c.started_at,c.updated_at,c.storage_path,c.repo_root,
                   COALESCE(m.count,0),COALESCE(f.count,0)
            FROM recent_conversations c
            LEFT JOIN message_counts m ON m.conversation_id=c.conversation_id
            LEFT JOIN file_counts f ON f.conversation_id=c.conversation_id
            ORDER BY c.updated_at DESC;
            """;
        var conversations = new List<WorkbenchConversationInsight>();
        await using (var reader =
            await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetString(0);
                var agent = reader.GetString(2);
                var conversation = new ConversationSummary(
                    id,
                    reader.GetString(1),
                    agent,
                    reader.GetString(3),
                    reader.GetString(4),
                    ParseDate(reader.GetString(5)),
                    ParseDate(reader.GetString(6)),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.GetString(8));
                conversations.Add(new WorkbenchConversationInsight(
                    conversation,
                    reader.GetInt32(9),
                    reader.GetInt32(10),
                    favoriteIds.Contains(id)
                        || favoriteKeys.Contains($"{agent}:{id}")));
            }
        }

        var highSignal = conversations
            .Where(value => value.FileCount > 0
                || value.MessageCount >= 12
                || value.IsFavorite)
            .OrderByDescending(value => value.SignalScore)
            .ThenByDescending(value => value.Conversation.UpdatedAt)
            .Take(3)
            .ToArray();
        var cutoff = (now ?? DateTimeOffset.UtcNow).AddDays(-90);
        var cleanup = conversations
            .Where(value => value.FileCount == 0
                && value.MessageCount <= 2
                && value.Conversation.UpdatedAt < cutoff
                && !value.IsFavorite)
            .OrderBy(value => value.Conversation.UpdatedAt)
            .Take(3)
            .ToArray();
        var latest = conversations.FirstOrDefault();
        var usesFileChanges = latest is { FileCount: > 0 };
        var recommendedAgent = latest is null
            ? ""
            : usesFileChanges
                ? "Codex"
                : latest.Conversation.SourceAgent;
        if (availableAgentIds is not null
            && !availableAgentIds.Contains(recommendedAgent))
        {
            recommendedAgent = "";
        }

        return new WorkbenchInsights(
            favorites.Count,
            await CountAsync(
                connection,
                "SELECT COUNT(*) FROM approved_memories WHERE status='active';",
                cancellationToken),
            await CountAsync(
                connection,
                """
                SELECT COUNT(*) FROM memory_candidates
                WHERE status IN ('pending','pending_review');
                """,
                cancellationToken),
            await CountAsync(
                connection,
                "SELECT COUNT(*) FROM wiki_pages WHERE status='active';",
                cancellationToken),
            recommendedAgent,
            usesFileChanges,
            highSignal,
            cleanup);
    }

    private static async Task<int> CountAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        string query,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = query;
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken));
    }

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.TryParse(value, out var parsed)
            ? parsed
            : DateTimeOffset.UnixEpoch;
}
