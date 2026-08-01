// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIMemory.Core.Models;
using AIMemory.Core.Persistence;

namespace AIMemory.Core.Services;

/// <summary>
/// Incremental sync for a user-selected cloud-backed folder.
///
/// The canonical layout is shared with the macOS application:
/// <c>&lt;selected folder&gt;/conversations/&lt;agent&gt;/&lt;base64url(id)&gt;.json</c>.
/// A root manifest is written only as status metadata; conversation discovery
/// always scans validated files. Previous Windows releases stored payloads in
/// <c>AIMemorySync/conversations</c>; that subfolder remains read-compatible,
/// while every new write goes to the canonical root.
/// </summary>
public sealed class LocalFolderSyncService(ConversationRepository conversations)
{
    private const int LayoutSchemaVersion = 3;
    private const string CanonicalLayout = "aimemory-local-folder-v1";
    private const string ConversationsFolder = "conversations";
    private const string LegacyWindowsFolder = "AIMemorySync";
    private const string ManifestFilename = "manifest.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public async Task<SyncProgress> SyncAsync(
        string folder,
        CancellationToken cancellationToken = default,
        IProgress<SyncProgress>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            throw new InvalidOperationException("同步文件夹不能为空。");
        }

        var root = Path.GetFullPath(folder);
        RefuseKnownDatabaseLockFiles(root);
        Directory.CreateDirectory(root);
        var conversationRoot = ChildPath(root, ConversationsFolder);
        Directory.CreateDirectory(conversationRoot);

        var local = new Dictionary<SyncKey, LocalPayload>();
        foreach (var summary in await conversations.ListAsync(
                     limit: 5_000,
                     cancellationToken: cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var detail = await conversations.ExportAsync(
                summary.Id, cancellationToken);
            if (!TryCreateKey(detail.SourceAgent, detail.Id, out var key))
            {
                throw new InvalidDataException(
                    $"对话 {detail.Id} 的来源标识不适合写入同步目录。");
            }
            local[key] = new LocalPayload(
                detail,
                JsonSerializer.SerializeToUtf8Bytes(detail, JsonOptions),
                SemanticHash(detail));
        }

        var remote = await ScanRemoteAsync(root, cancellationToken);
        var uploaded = 0;
        var downloaded = 0;
        var skipped = 0;
        foreach (var key in local.Keys.Union(remote.Keys)
                     .OrderBy(value => value.Agent, StringComparer.Ordinal)
                     .ThenBy(value => value.Id, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            local.TryGetValue(key, out var localValue);
            remote.TryGetValue(key, out var remoteValue);
            if (localValue is not null && remoteValue is null)
            {
                await WriteLocalAsync(
                    conversationRoot, key, localValue, cancellationToken);
                uploaded++;
            }
            else if (localValue is null && remoteValue is not null)
            {
                await conversations.UpsertAsync(
                    remoteValue.Detail, cancellationToken);
                downloaded++;
            }
            else if (localValue is not null && remoteValue is not null)
            {
                if (string.Equals(
                        remoteValue.Hash,
                        localValue.Hash,
                        StringComparison.OrdinalIgnoreCase))
                {
                    skipped++;
                }
                else if (IsNewer(
                             remoteValue.Detail.UpdatedAt,
                             localValue.Detail.UpdatedAt))
                {
                    await conversations.UpsertAsync(
                        remoteValue.Detail, cancellationToken);
                    downloaded++;
                }
                else
                {
                    // Equal timestamps with real content changes preserve the
                    // existing local-wins policy. Semantic hashes ignore
                    // serializer-only fields so macOS and Windows do not
                    // continuously overwrite each other.
                    await WriteLocalAsync(
                        conversationRoot, key, localValue, cancellationToken);
                    uploaded++;
                }
            }

            progress?.Report(new SyncProgress(
                "conversations",
                uploaded,
                downloaded,
                skipped,
                false,
                $"正在同步 {key.Agent} · {key.Id}",
                key.Agent,
                key.Id));
        }

        await WriteStatusManifestAsync(
            root,
            uploaded,
            downloaded,
            skipped,
            local.Count,
            remote.Count,
            cancellationToken);
        var result = new SyncProgress(
            "complete", uploaded, downloaded, skipped, true,
            $"本地同步完成：上传 {uploaded}，下载 {downloaded}，跳过 {skipped}。");
        progress?.Report(result);
        return result;
    }

    private static async Task<Dictionary<SyncKey, RemotePayload>> ScanRemoteAsync(
        string root,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<SyncKey, RemotePayload>();
        await ScanLayoutAsync(
            root,
            ChildPath(root, ConversationsFolder),
            layoutPriority: 2,
            result,
            cancellationToken);
        await ScanLayoutAsync(
            root,
            ChildPath(root, LegacyWindowsFolder, ConversationsFolder),
            layoutPriority: 0,
            result,
            cancellationToken);
        return result;
    }

    private static async Task ScanLayoutAsync(
        string root,
        string conversationsRoot,
        int layoutPriority,
        IDictionary<SyncKey, RemotePayload> result,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(conversationsRoot)
            || IsReparsePoint(conversationsRoot))
        {
            return;
        }

        IEnumerable<string> agentFolders;
        try
        {
            agentFolders = Directory.EnumerateDirectories(
                conversationsRoot,
                "*",
                SearchOption.TopDirectoryOnly)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        foreach (var agentFolder in agentFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsReparsePoint(agentFolder)
                || !TryNormalizeAgent(Path.GetFileName(agentFolder), out var directoryAgent))
            {
                continue;
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(
                    agentFolder,
                    "*",
                    SearchOption.TopDirectoryOnly)
                    .Where(value => Path.GetExtension(value).Equals(
                        ".json", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray();
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsReparsePoint(file)) continue;

                WebDavConversationDetail? detail;
                try
                {
                    var data = await File.ReadAllBytesAsync(
                        file, cancellationToken);
                    detail = JsonSerializer.Deserialize<WebDavConversationDetail>(
                        data, JsonOptions);
                }
                catch (IOException)
                {
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }
                catch (JsonException)
                {
                    continue;
                }

                if (detail is null
                    || !TryCreateKey(detail.SourceAgent, detail.Id, out var key)
                    || !key.Agent.Equals(directoryAgent, StringComparison.Ordinal))
                {
                    continue;
                }

                var canonicalFilename = Base64Url(key.Id) + ".json";
                var candidate = new RemotePayload(
                    detail,
                    SemanticHash(detail),
                    layoutPriority + (Path.GetFileName(file).Equals(
                        canonicalFilename, StringComparison.Ordinal) ? 1 : 0),
                    Path.GetRelativePath(root, file).Replace('\\', '/'));
                if (!result.TryGetValue(key, out var existing)
                    || ShouldReplace(existing, candidate))
                {
                    result[key] = candidate;
                }
            }
        }
    }

    private static bool ShouldReplace(
        RemotePayload existing,
        RemotePayload candidate)
    {
        if (IsNewer(candidate.Detail.UpdatedAt, existing.Detail.UpdatedAt))
        {
            return true;
        }
        if (IsNewer(existing.Detail.UpdatedAt, candidate.Detail.UpdatedAt))
        {
            return false;
        }
        if (candidate.Priority != existing.Priority)
        {
            return candidate.Priority > existing.Priority;
        }
        // Deterministic handling for duplicate legacy files avoids repeated
        // import decisions when a cloud provider changes enumeration order.
        return string.Compare(
            candidate.StablePath,
            existing.StablePath,
            StringComparison.Ordinal) < 0;
    }

    private static async Task WriteLocalAsync(
        string conversationRoot,
        SyncKey key,
        LocalPayload payload,
        CancellationToken cancellationToken)
    {
        var agentRoot = ChildPath(conversationRoot, key.Agent);
        Directory.CreateDirectory(agentRoot);
        var path = ChildPath(agentRoot, Base64Url(key.Id) + ".json");
        await AtomicWriteAsync(path, payload.Data, cancellationToken);
    }

    private static async Task WriteStatusManifestAsync(
        string root,
        int uploaded,
        int downloaded,
        int skipped,
        int totalLocal,
        int totalRemote,
        CancellationToken cancellationToken)
    {
        var manifest = new Dictionary<string, object?>
        {
            ["schema_version"] = LayoutSchemaVersion,
            ["layout"] = CanonicalLayout,
            ["last_synced_at"] = DateTimeOffset.UtcNow.ToString(
                "O", CultureInfo.InvariantCulture),
            ["sync_direction"] = "bidirectional",
            ["uploaded"] = uploaded,
            ["downloaded"] = downloaded,
            ["skipped"] = skipped,
            ["conflicts_resolved"] = 0,
            ["total_local"] = totalLocal,
            ["total_remote"] = totalRemote,
        };
        await AtomicWriteAsync(
            ChildPath(root, ManifestFilename),
            JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions),
            cancellationToken);
    }

    private static async Task AtomicWriteAsync(
        string path,
        byte[] data,
        CancellationToken cancellationToken)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, data, cancellationToken);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string ChildPath(string root, params string[] components)
    {
        if (components.Length == 0 || components.Any(value => !IsSafePathComponent(value)))
        {
            throw new InvalidDataException("同步路径包含不安全的目录组件。");
        }
        var canonicalRoot = Path.GetFullPath(root);
        var path = Path.GetFullPath(Path.Combine(
            [canonicalRoot, .. components]));
        var prefix = canonicalRoot.EndsWith(Path.DirectorySeparatorChar)
            || canonicalRoot.EndsWith(Path.AltDirectorySeparatorChar)
            ? canonicalRoot
            : canonicalRoot + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("同步路径越出所选文件夹。");
        }
        return path;
    }

    private static bool TryCreateKey(
        string agent,
        string id,
        out SyncKey key)
    {
        key = default;
        if (string.IsNullOrWhiteSpace(id)
            || id.IndexOf('\0') >= 0
            || !TryNormalizeAgent(agent, out var normalizedAgent))
        {
            return false;
        }
        key = new SyncKey(normalizedAgent, id);
        return true;
    }

    private static bool TryNormalizeAgent(string value, out string normalized)
    {
        normalized = value.Trim().ToLowerInvariant();
        return IsSafePathComponent(normalized)
            && normalized.All(value =>
                value is >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '-' or '_' or '.');
    }

    private static bool IsSafePathComponent(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value is not "." and not ".."
        && value.IndexOfAny(['/', '\\', '\0']) < 0;

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static void RefuseKnownDatabaseLockFiles(string folder)
    {
        var names = new[]
        {
            "aimemory.db-wal", "aimemory.db-shm",
            "chatmem.db-wal", "chatmem.db-shm",
        };
        if (names.Any(name => File.Exists(Path.Combine(folder, name))))
        {
            throw new IOException(
                "同步目录中存在数据库锁文件；请改用增量同步目录，不要直接同步运行中的数据库。");
        }
    }

    private static string SemanticHash(WebDavConversationDetail conversation)
    {
        using var writer = new CanonicalHashWriter();
        writer.AppendAscii("aimemory-local-sync-semantic-v1");
        writer.AppendString(conversation.Id);
        writer.AppendString(TryNormalizeAgent(
            conversation.SourceAgent, out var agent)
            ? agent
            : conversation.SourceAgent);
        writer.AppendString(conversation.ProjectDir);
        writer.AppendString(conversation.CreatedAt);
        writer.AppendString(conversation.UpdatedAt);
        writer.AppendString(conversation.Summary ?? "");
        writer.AppendString(conversation.StoragePath ?? "");

        var messages = conversation.Messages ?? [];
        writer.AppendCount(messages.Count);
        foreach (var message in messages)
        {
            writer.AppendString(message.Id);
            writer.AppendString(message.Timestamp);
            writer.AppendString(message.Role);
            writer.AppendString(message.Content);
            var toolCalls = message.ToolCalls ?? [];
            writer.AppendCount(toolCalls.Count);
            foreach (var tool in toolCalls)
            {
                writer.AppendString(tool.Id);
                writer.AppendString(tool.Name);
                writer.AppendJson(tool.Input);
                writer.AppendString(tool.Output ?? "");
                writer.AppendString(tool.Status);
            }
        }

        var changes = conversation.FileChanges ?? [];
        writer.AppendCount(changes.Count);
        foreach (var change in changes)
        {
            writer.AppendString(change.Path);
            writer.AppendString(change.ChangeType);
            writer.AppendString(change.Timestamp);
            writer.AppendString(change.MessageId ?? "");
        }
        return writer.Finish();
    }

    private static bool IsNewer(string left, string right)
    {
        var leftDate = TryParseDate(left);
        if (leftDate is null) return false;
        var rightDate = TryParseDate(right);
        return rightDate is null || leftDate > rightDate;
    }

    private static DateTimeOffset? TryParseDate(string value) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces
                | DateTimeStyles.AssumeUniversal
                | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;

    private static string Base64Url(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private readonly record struct SyncKey(string Agent, string Id);

    private sealed record LocalPayload(
        WebDavConversationDetail Detail,
        byte[] Data,
        string Hash);

    private sealed record RemotePayload(
        WebDavConversationDetail Detail,
        string Hash,
        int Priority,
        string StablePath);

    /// <summary>
    /// Binary length-prefixed canonicalizer shared with the macOS service. It
    /// avoids JSON formatting/property-order differences while retaining every
    /// persisted conversation field, including nested tool input.
    /// </summary>
    private sealed class CanonicalHashWriter : IDisposable
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);

        public void AppendAscii(string value) =>
            _hash.AppendData(Encoding.UTF8.GetBytes(value));

        public void AppendCount(int value) => AppendAscii($"c{value}:");

        public void AppendString(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            AppendAscii($"s{bytes.Length}:");
            _hash.AppendData(bytes);
        }

        public void AppendJson(JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    AppendAscii("n");
                    break;
                case JsonValueKind.True:
                    AppendAscii("b1");
                    break;
                case JsonValueKind.False:
                    AppendAscii("b0");
                    break;
                case JsonValueKind.Number:
                    try
                    {
                        var bits = unchecked((ulong)BitConverter.DoubleToInt64Bits(
                            value.GetDouble()));
                        AppendAscii("d" + bits.ToString("x16", CultureInfo.InvariantCulture));
                    }
                    catch (FormatException)
                    {
                        AppendAscii("r");
                        AppendString(value.GetRawText());
                    }
                    break;
                case JsonValueKind.String:
                    AppendString(value.GetString() ?? "");
                    break;
                case JsonValueKind.Array:
                    AppendAscii("a");
                    AppendCount(value.GetArrayLength());
                    foreach (var item in value.EnumerateArray()) AppendJson(item);
                    break;
                case JsonValueKind.Object:
                    var properties = value.EnumerateObject()
                        .OrderBy(item => item.Name, Utf8StringComparer.Instance)
                        .ToArray();
                    AppendAscii("o");
                    AppendCount(properties.Length);
                    foreach (var property in properties)
                    {
                        AppendString(property.Name);
                        AppendJson(property.Value);
                    }
                    break;
                default:
                    AppendAscii("n");
                    break;
            }
        }

        public string Finish() => Convert.ToHexString(
            _hash.GetHashAndReset()).ToLowerInvariant();

        public void Dispose() => _hash.Dispose();
    }

    private sealed class Utf8StringComparer : IComparer<string>
    {
        public static readonly Utf8StringComparer Instance = new();

        public int Compare(string? left, string? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            return Encoding.UTF8.GetBytes(left).AsSpan()
                .SequenceCompareTo(Encoding.UTF8.GetBytes(right));
        }
    }
}
