using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIMemory.Core.Models;
using AIMemory.Core.Persistence;

namespace AIMemory.Core.Services;

public sealed class LocalFolderSyncService(ConversationRepository conversations)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public async Task<SyncProgress> SyncAsync(
        string folder,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            throw new InvalidOperationException("同步文件夹不能为空。");
        }
        var root = Path.Combine(folder, "AIMemorySync");
        var conversationRoot = Path.Combine(root, "conversations");
        Directory.CreateDirectory(conversationRoot);
        RefuseKnownDatabaseLockFiles(folder);

        var manifestPath = Path.Combine(root, "manifest.json");
        var manifest = File.Exists(manifestPath)
            ? JsonSerializer.Deserialize<WebDavManifest>(
                await File.ReadAllTextAsync(manifestPath, cancellationToken),
                JsonOptions) ?? new WebDavManifest(0, "", [])
            : new WebDavManifest(0, "", []);
        var remote = manifest.Conversations.ToDictionary(
            value => (value.Agent, value.Id));
        var local = new Dictionary<
            (string Agent, string Id),
            (WebDavConversationDetail Detail, byte[] Data, string Hash)>();
        foreach (var summary in await conversations.ListAsync(
                     limit: 5_000,
                     cancellationToken: cancellationToken))
        {
            var detail = await conversations.ExportAsync(summary.Id, cancellationToken);
            var data = JsonSerializer.SerializeToUtf8Bytes(detail, JsonOptions);
            local[(detail.SourceAgent, detail.Id)] = (detail, data, Hash(data));
        }

        var merged = new Dictionary<(string Agent, string Id), WebDavManifestEntry>();
        var uploaded = 0;
        var downloaded = 0;
        var skipped = 0;
        foreach (var key in local.Keys.Union(remote.Keys)
                     .OrderBy(value => value.Agent)
                     .ThenBy(value => value.Id))
        {
            local.TryGetValue(key, out var localValue);
            remote.TryGetValue(key, out var remoteValue);
            if (localValue.Detail is not null && remoteValue is null)
            {
                merged[key] = await WriteLocalAsync(
                    root, key, localValue, cancellationToken);
                uploaded++;
            }
            else if (localValue.Detail is null && remoteValue is not null)
            {
                merged[key] = await ReadRemoteAsync(
                    root, remoteValue, cancellationToken);
                downloaded++;
            }
            else if (localValue.Detail is not null && remoteValue is not null)
            {
                if (string.Equals(
                        remoteValue.Sha256,
                        localValue.Hash,
                        StringComparison.OrdinalIgnoreCase)
                    || (remoteValue.Sha256 is null
                        && remoteValue.UpdatedAt == localValue.Detail.UpdatedAt))
                {
                    merged[key] = remoteValue with { Sha256 = localValue.Hash };
                    skipped++;
                }
                else if (ParseDate(remoteValue.UpdatedAt)
                         > ParseDate(localValue.Detail.UpdatedAt))
                {
                    merged[key] = await ReadRemoteAsync(
                        root, remoteValue, cancellationToken);
                    downloaded++;
                }
                else
                {
                    merged[key] = await WriteLocalAsync(
                        root, key, localValue, cancellationToken);
                    uploaded++;
                }
            }
        }

        var updated = new WebDavManifest(
            2,
            DateTimeOffset.UtcNow.ToString("O"),
            merged.Values.OrderBy(value => value.Agent)
                .ThenBy(value => value.Id).ToArray());
        await AtomicWriteAsync(
            manifestPath,
            JsonSerializer.SerializeToUtf8Bytes(updated, JsonOptions),
            cancellationToken);
        return new SyncProgress(
            "complete", uploaded, downloaded, skipped, true,
            $"本地同步完成：上传 {uploaded}，下载 {downloaded}，跳过 {skipped}。");
    }

    private static async Task<WebDavManifestEntry> WriteLocalAsync(
        string root,
        (string Agent, string Id) key,
        (WebDavConversationDetail Detail, byte[] Data, string Hash) payload,
        CancellationToken cancellationToken)
    {
        var relative = Path.Combine(
            "conversations", key.Agent, Base64Url(key.Id) + ".json");
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await AtomicWriteAsync(path, payload.Data, cancellationToken);
        return new WebDavManifestEntry(
            key.Agent, key.Id,
            relative.Replace('\\', '/'),
            payload.Detail.UpdatedAt,
            payload.Hash);
    }

    private async Task<WebDavManifestEntry> ReadRemoteAsync(
        string root,
        WebDavManifestEntry entry,
        CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(
            Path.Combine(root, entry.File.Replace('/', Path.DirectorySeparatorChar)));
        var canonicalRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("同步 manifest 包含越界路径。");
        }
        var data = await File.ReadAllBytesAsync(path, cancellationToken);
        var detail = JsonSerializer.Deserialize<WebDavConversationDetail>(
            data, JsonOptions) ?? throw new InvalidDataException("同步对话无法解析。");
        await conversations.UpsertAsync(detail, cancellationToken);
        return entry with { Sha256 = Hash(data) };
    }

    private static async Task AtomicWriteAsync(
        string path,
        byte[] data,
        CancellationToken cancellationToken)
    {
        var temporary = path + ".tmp";
        await File.WriteAllBytesAsync(temporary, data, cancellationToken);
        File.Move(temporary, path, true);
    }

    private static void RefuseKnownDatabaseLockFiles(string folder)
    {
        var names = new[] { "aimemory.db-wal", "aimemory.db-shm", "chatmem.db-wal", "chatmem.db-shm" };
        if (names.Any(name => File.Exists(Path.Combine(folder, name))))
        {
            throw new IOException("同步目录中存在数据库锁文件；请改用增量同步目录，不要直接同步运行中的数据库。");
        }
    }

    private static string Hash(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static string Base64Url(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.TryParse(value, out var parsed)
            ? parsed
            : DateTimeOffset.UnixEpoch;
}
