using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIMemory.Core.Persistence;
using Microsoft.Data.Sqlite;

namespace AIMemory.Core.Services;

public sealed record MemoryConflictRecord(
    string Id,
    string CandidateId,
    string MemoryId,
    string MemoryTitle,
    string Reason,
    string Status,
    string CreatedAt);

public sealed record MemoryEntityNode(
    string Id,
    string Name,
    string Kind,
    int MentionCount);

public sealed record MemoryEntityLink(
    string EntityId,
    string EntityName,
    string OwnerType,
    string OwnerId,
    string Relationship,
    string SourceTitle,
    string? SourceConversationId);

public sealed record MemoryEntityGraph(
    IReadOnlyList<MemoryEntityNode> Entities,
    IReadOnlyList<MemoryEntityLink> Links);

public sealed record IndexRebuildResult(
    int DocumentCount,
    int EmbeddingCount);

public sealed class KnowledgeProjectionService(
    AIMemoryDatabase database,
    RepositoryGovernanceService governance)
{
    public async Task<IReadOnlyList<WikiRecord>> RebuildWikiAsync(
        string repoRoot,
        CancellationToken cancellationToken = default)
    {
        var repoId = await governance.ResolveRepoIdAsync(
            repoRoot,
            create: true,
            cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("无法创建仓库记录。");
        await using var connection = database.OpenConnection();
        var memories = new List<WikiMemory>();
        var memoryCommand = connection.CreateCommand();
        memoryCommand.CommandText = """
            SELECT memory_id,kind,title,value,usage_hint
            FROM approved_memories
            WHERE repo_id=$repo AND status='active'
            ORDER BY kind,title COLLATE NOCASE;
            """;
        memoryCommand.Parameters.AddWithValue("$repo", repoId);
        await using (var reader =
                     await memoryCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                memories.Add(new WikiMemory(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4)));
            }
        }

        var episodes = new List<WikiEpisode>();
        var episodeCommand = connection.CreateCommand();
        episodeCommand.CommandText = """
            SELECT episode_id,title,summary,outcome
            FROM episodes
            WHERE repo_id=$repo
            ORDER BY created_at DESC;
            """;
        episodeCommand.Parameters.AddWithValue("$repo", repoId);
        await using (var reader =
                     await episodeCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                episodes.Add(new WikiEpisode(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3)));
            }
        }

        var now = DateTimeOffset.UtcNow.ToString("O");
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(
                cancellationToken);
        var clear = connection.CreateCommand();
        clear.Transaction = transaction;
        clear.CommandText =
            "DELETE FROM wiki_pages WHERE repo_id=$repo AND status='generated';";
        clear.Parameters.AddWithValue("$repo", repoId);
        await clear.ExecuteNonQueryAsync(cancellationToken);

        foreach (var group in memories
                     .GroupBy(value => value.Kind)
                     .OrderBy(value => value.Key, StringComparer.Ordinal))
        {
            var body = string.Join(
                "\n\n",
                group.Select(value =>
                    $"## {value.Title}\n\n{value.Value}"
                    + (string.IsNullOrWhiteSpace(value.UsageHint)
                        ? ""
                        : $"\n\n使用提示：{value.UsageHint}")));
            await InsertWikiPageAsync(
                connection,
                transaction,
                $"wiki-{repoId}-{group.Key}",
                repoId,
                group.Key,
                WikiTitle(group.Key),
                body,
                JsonSerializer.Serialize(group.Select(value => value.Id)),
                "[]",
                now,
                cancellationToken);
        }

        if (episodes.Count > 0)
        {
            var body = string.Join(
                "\n\n",
                episodes.Select(value =>
                    $"## {value.Title}\n\n{value.Summary}"
                    + (string.IsNullOrWhiteSpace(value.Outcome)
                        ? ""
                        : $"\n\n结果：{value.Outcome}")));
            await InsertWikiPageAsync(
                connection,
                transaction,
                $"wiki-{repoId}-episodes",
                repoId,
                "episodes",
                "项目经历",
                body,
                "[]",
                JsonSerializer.Serialize(episodes.Select(value => value.Id)),
                now,
                cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return await ListWikiAsync(repoId, cancellationToken);
    }

    public async Task<IndexRebuildResult> RebuildSearchIndexAsync(
        string repoRoot,
        CancellationToken cancellationToken = default)
    {
        var repoId = await governance.ResolveRepoIdAsync(
            repoRoot,
            create: true,
            cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("无法创建仓库记录。");
        await using var connection = database.OpenConnection();
        var documents = new List<SearchDocument>();

        var conversations = connection.CreateCommand();
        conversations.CommandText = """
            SELECT c.conversation_id,
                   COALESCE(c.summary,c.conversation_id),
                   COALESCE(group_concat(m.content,char(10)),'')
            FROM conversations c
            LEFT JOIN messages m ON m.conversation_id=c.conversation_id
            WHERE c.repo_id=$repo
            GROUP BY c.conversation_id;
            """;
        conversations.Parameters.AddWithValue("$repo", repoId);
        await using (var reader =
                     await conversations.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var reference = reader.GetString(0);
                documents.Add(new SearchDocument(
                    $"conversation:{reference}",
                    "conversation",
                    reference,
                    reader.GetString(1),
                    reader.GetString(2)));
            }
        }

        var memories = connection.CreateCommand();
        memories.CommandText = """
            SELECT memory_id,title,value,usage_hint
            FROM approved_memories
            WHERE repo_id=$repo AND status='active';
            """;
        memories.Parameters.AddWithValue("$repo", repoId);
        await using (var reader =
                     await memories.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var reference = reader.GetString(0);
                documents.Add(new SearchDocument(
                    $"memory:{reference}",
                    "memory",
                    reference,
                    reader.GetString(1),
                    $"{reader.GetString(2)}\n{reader.GetString(3)}"));
            }
        }

        var wiki = connection.CreateCommand();
        wiki.CommandText =
            "SELECT page_id,title,body FROM wiki_pages WHERE repo_id=$repo;";
        wiki.Parameters.AddWithValue("$repo", repoId);
        await using (var reader =
                     await wiki.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var reference = reader.GetString(0);
                documents.Add(new SearchDocument(
                    $"wiki:{reference}",
                    "wiki",
                    reference,
                    reader.GetString(1),
                    reader.GetString(2)));
            }
        }

        var oldIds = new List<string>();
        var old = connection.CreateCommand();
        old.CommandText =
            "SELECT doc_id FROM search_documents WHERE repo_id=$repo;";
        old.Parameters.AddWithValue("$repo", repoId);
        await using (var reader =
                     await old.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                oldIds.Add(reader.GetString(0));
            }
        }

        var now = DateTimeOffset.UtcNow.ToString("O");
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(
                cancellationToken);
        foreach (var oldId in oldIds)
        {
            var deleteFts = connection.CreateCommand();
            deleteFts.Transaction = transaction;
            deleteFts.CommandText =
                "DELETE FROM search_documents_fts WHERE doc_id=$id;";
            deleteFts.Parameters.AddWithValue("$id", oldId);
            await deleteFts.ExecuteNonQueryAsync(cancellationToken);
        }
        await DeleteRepoRowsAsync(
            connection,
            transaction,
            "document_embeddings",
            repoId,
            cancellationToken);
        await DeleteRepoRowsAsync(
            connection,
            transaction,
            "search_documents",
            repoId,
            cancellationToken);

        foreach (var document in documents)
        {
            var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO search_documents(
                  doc_id,repo_id,doc_type,doc_ref_id,title,body,updated_at)
                VALUES($id,$repo,$type,$ref,$title,$body,$now);
                INSERT INTO search_documents_fts(doc_id,title,body)
                VALUES($id,$title,$body);
                INSERT INTO document_embeddings(
                  doc_id,repo_id,embedding_model,dimensions,vector_json,updated_at)
                VALUES($id,$repo,'native-token-hash-v1',128,$vector,$now);
                """;
            insert.Parameters.AddWithValue("$id", document.Id);
            insert.Parameters.AddWithValue("$repo", repoId);
            insert.Parameters.AddWithValue("$type", document.Type);
            insert.Parameters.AddWithValue("$ref", document.Reference);
            insert.Parameters.AddWithValue("$title", document.Title);
            insert.Parameters.AddWithValue("$body", document.Body);
            insert.Parameters.AddWithValue(
                "$vector",
                JsonSerializer.Serialize(TokenHashVector(
                    $"{document.Title}\n{document.Body}")));
            insert.Parameters.AddWithValue("$now", now);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new IndexRebuildResult(documents.Count, documents.Count);
    }

    public async Task<IReadOnlyList<MemoryConflictRecord>> ListConflictsAsync(
        string repoRoot,
        string? status,
        CancellationToken cancellationToken = default)
    {
        var repoId = await governance.ResolveRepoIdAsync(
            repoRoot,
            cancellationToken: cancellationToken);
        if (repoId is null) return [];
        await using var connection = database.OpenConnection();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT mc.conflict_id,mc.candidate_id,mc.memory_id,
                   am.title,mc.reason,mc.status,mc.created_at
            FROM memory_conflicts mc
            JOIN approved_memories am ON am.memory_id=mc.memory_id
            WHERE mc.repo_id=$repo
              AND ($status IS NULL OR mc.status=$status)
            ORDER BY mc.created_at DESC;
            """;
        command.Parameters.AddWithValue("$repo", repoId);
        command.Parameters.AddWithValue(
            "$status",
            string.IsNullOrWhiteSpace(status) ? DBNull.Value : status.Trim());
        var result = new List<MemoryConflictRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new MemoryConflictRecord(
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

    public async Task<MemoryEntityGraph> ListEntityGraphAsync(
        string repoRoot,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var repoId = await governance.ResolveRepoIdAsync(
            repoRoot,
            cancellationToken: cancellationToken);
        if (repoId is null) return new MemoryEntityGraph([], []);
        var bounded = Math.Clamp(limit, 1, 100);
        await using var connection = database.OpenConnection();
        var entityCommand = connection.CreateCommand();
        entityCommand.CommandText = """
            SELECT e.entity_id,e.name,e.kind,COUNT(l.link_id)
            FROM memory_entities e
            LEFT JOIN memory_entity_links l ON l.entity_id=e.entity_id
            WHERE e.repo_id=$repo
            GROUP BY e.entity_id,e.name,e.kind
            ORDER BY COUNT(l.link_id) DESC,e.updated_at DESC
            LIMIT $limit;
            """;
        entityCommand.Parameters.AddWithValue("$repo", repoId);
        entityCommand.Parameters.AddWithValue("$limit", bounded);
        var entities = new List<MemoryEntityNode>();
        await using (var reader =
                     await entityCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                entities.Add(new MemoryEntityNode(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt32(3)));
            }
        }
        if (entities.Count == 0) return new MemoryEntityGraph([], []);

        var selected = entities.Select(value => value.Id)
            .ToHashSet(StringComparer.Ordinal);
        var linkCommand = connection.CreateCommand();
        linkCommand.CommandText = """
            SELECT l.entity_id,e.name,l.owner_type,l.owner_id,l.relationship,
                   COALESCE(sd.title,cc.title,l.owner_id),
                   cc.conversation_id
            FROM memory_entity_links l
            JOIN memory_entities e ON e.entity_id=l.entity_id
            LEFT JOIN conversation_chunks cc
              ON l.owner_type='chunk' AND cc.chunk_id=l.owner_id
            LEFT JOIN search_documents sd
              ON sd.repo_id=l.repo_id AND sd.doc_ref_id=l.owner_id
             AND ((l.owner_type='memory' AND sd.doc_type='memory')
               OR (l.owner_type='episode' AND sd.doc_type='episode')
               OR (l.owner_type='wiki_page' AND sd.doc_type='wiki')
               OR (l.owner_type='conversation' AND sd.doc_type='conversation'))
            WHERE l.repo_id=$repo
            ORDER BY l.created_at DESC
            LIMIT $limit;
            """;
        linkCommand.Parameters.AddWithValue("$repo", repoId);
        linkCommand.Parameters.AddWithValue("$limit", bounded * 4);
        var links = new List<MemoryEntityLink>();
        await using var linkReader =
            await linkCommand.ExecuteReaderAsync(cancellationToken);
        while (await linkReader.ReadAsync(cancellationToken))
        {
            if (!selected.Contains(linkReader.GetString(0))) continue;
            links.Add(new MemoryEntityLink(
                linkReader.GetString(0),
                linkReader.GetString(1),
                linkReader.GetString(2),
                linkReader.GetString(3),
                linkReader.GetString(4),
                linkReader.GetString(5),
                linkReader.IsDBNull(6) ? null : linkReader.GetString(6)));
        }
        return new MemoryEntityGraph(entities, links);
    }

    private static async Task InsertWikiPageAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string id,
        string repoId,
        string slug,
        string title,
        string body,
        string memoryIds,
        string episodeIds,
        string now,
        CancellationToken cancellationToken)
    {
        var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO wiki_pages(
              page_id,repo_id,slug,title,body,status,
              source_memory_ids_json,source_episode_ids_json,
              last_built_at,last_verified_at,created_at,updated_at)
            VALUES($id,$repo,$slug,$title,$body,'generated',
                   $memories,$episodes,$now,NULL,$now,$now);
            """;
        insert.Parameters.AddWithValue("$id", id);
        insert.Parameters.AddWithValue("$repo", repoId);
        insert.Parameters.AddWithValue("$slug", slug);
        insert.Parameters.AddWithValue("$title", title);
        insert.Parameters.AddWithValue("$body", body);
        insert.Parameters.AddWithValue("$memories", memoryIds);
        insert.Parameters.AddWithValue("$episodes", episodeIds);
        insert.Parameters.AddWithValue("$now", now);
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<WikiRecord>> ListWikiAsync(
        string repoId,
        CancellationToken cancellationToken)
    {
        return (await new HistoryProjectionService(database)
                .ListWikiAsync(cancellationToken))
            .Where(value => value.RepoId == repoId)
            .ToArray();
    }

    private static async Task DeleteRepoRowsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string repoId,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"DELETE FROM {table} WHERE repo_id=$repo;";
        command.Parameters.AddWithValue("$repo", repoId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string WikiTitle(string kind) =>
        kind.ToLowerInvariant() switch
        {
            "command" => "常用命令",
            "convention" => "项目约定",
            "decision" => "关键决策",
            "gotcha" => "注意事项",
            "preference" => "协作偏好",
            _ => kind,
        };

    private static double[] TokenHashVector(string text)
    {
        var vector = new double[128];
        foreach (var token in Regex.Split(
                     text.ToLowerInvariant(),
                     @"[^\p{L}\p{N}]+"))
        {
            if (string.IsNullOrEmpty(token)) continue;
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(token));
            var index = digest[0] % vector.Length;
            vector[index] += (digest[1] & 1) == 0 ? 1 : -1;
        }
        var norm = Math.Sqrt(vector.Sum(value => value * value));
        if (norm == 0) return vector;
        for (var index = 0; index < vector.Length; index++)
        {
            vector[index] /= norm;
        }
        return vector;
    }

    private sealed record WikiMemory(
        string Id,
        string Kind,
        string Title,
        string Value,
        string UsageHint);

    private sealed record WikiEpisode(
        string Id,
        string Title,
        string Summary,
        string Outcome);

    private sealed record SearchDocument(
        string Id,
        string Type,
        string Reference,
        string Title,
        string Body);
}
