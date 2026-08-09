// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
using System.Buffers.Binary;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIMemory.Core.Models;
using AIMemory.Core.Persistence;

namespace AIMemory.Core.Services;

public sealed class WebDavService(
    ConversationRepository? conversations = null,
    HttpClient? httpClient = null)
{
    private sealed record LocalConversationPayload(
        string Hash,
        string SemanticDigest,
        string UpdatedAt);

    private readonly HttpClient _client = httpClient ?? new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(30),
    };
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public async Task<int> VerifyAsync(
        Uri collection,
        string? username,
        string? password,
        CancellationToken cancellationToken = default)
    {
        using var request = Request(
            new HttpMethod("PROPFIND"), collection, username, password);
        request.Headers.Add("Depth", "0");
        using var response = await _client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"WebDAV 验证失败：HTTP {(int)response.StatusCode}",
                null,
                response.StatusCode);
        }
        return (int)response.StatusCode;
    }

    public async Task<SyncProgress> SyncAsync(
        Uri root,
        string? username,
        string? password,
        IProgress<SyncProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (conversations is null)
        {
            throw new InvalidOperationException("同步服务缺少对话仓库。");
        }

        await EnsureCollectionAsync(root, username, password, cancellationToken);
        var conversationRoot = new Uri(root, "conversations/");
        await EnsureCollectionAsync(
            conversationRoot, username, password, cancellationToken);
        var remoteManifest = await LoadManifestAsync(
            root, username, password, cancellationToken);
        var remote = remoteManifest.Conversations.ToDictionary(
            value => (value.Agent, value.Id));

        var local = new Dictionary<
            (string Agent, string Id),
            LocalConversationPayload>();
        foreach (var summary in await conversations.ListAsync(
                     limit: 5_000,
                     cancellationToken: cancellationToken))
        {
            var detail = await conversations.ExportAsync(
                summary.Id, cancellationToken);
            var data = JsonSerializer.SerializeToUtf8Bytes(detail, JsonOptions);
            local[(detail.SourceAgent, detail.Id)] = new LocalConversationPayload(
                Hash(data),
                SemanticDigest(detail),
                detail.UpdatedAt);
        }

        var keys = local.Keys.Union(remote.Keys)
            .OrderBy(value => value.Agent, StringComparer.Ordinal)
            .ThenBy(value => value.Id, StringComparer.Ordinal)
            .ToArray();
        var merged = new Dictionary<(string Agent, string Id), WebDavManifestEntry>();
        var ensuredAgents = new HashSet<string>(StringComparer.Ordinal);
        var uploaded = 0;
        var downloaded = 0;
        var skipped = 0;

        foreach (var key in keys)
        {
            var hasLocal = local.TryGetValue(key, out var localValue);
            remote.TryGetValue(key, out var remoteValue);
            if (hasLocal && remoteValue is null)
            {
                merged[key] = await UploadAsync(
                    conversationRoot, key,
                    ensuredAgents, username, password, cancellationToken);
                uploaded++;
            }
            else if (!hasLocal && remoteValue is not null)
            {
                merged[key] = await DownloadAsync(
                    root, remoteValue, username, password, cancellationToken);
                downloaded++;
            }
            else if (hasLocal && remoteValue is not null)
            {
                var remoteSemanticDigest = IsCurrentSemanticDigest(
                    remoteValue.SemanticDigest)
                    ? remoteValue.SemanticDigest
                    : null;
                if (string.Equals(
                        remoteSemanticDigest,
                        localValue!.SemanticDigest,
                        StringComparison.Ordinal))
                {
                    // The versioned semantic digest is shared by Foundation
                    // and System.Text.Json, so equivalent JSON never uploads
                    // merely because the two serializers format bytes
                    // differently.
                    merged[key] = remoteValue;
                    skipped++;
                }
                else if (string.Equals(
                        remoteValue.Sha256,
                        localValue!.Hash,
                        StringComparison.OrdinalIgnoreCase))
                {
                    // A legacy byte hash is already conclusive. Keep its
                    // unchanged manifest entry intact rather than rewriting
                    // every legacy snapshot solely to append an optional field.
                    merged[key] = remoteValue;
                    skipped++;
                }
                else if (remoteSemanticDigest is null)
                {
                    // Legacy schema-v1/v2 entries cannot use an equal timestamp
                    // as proof of equality. Read once, calculate the shared
                    // digest, and preserve the established local-wins behavior
                    // when logically different snapshots have the same time.
                    var payload = await ReadRemotePayloadAsync(
                        root, remoteValue, username, password, cancellationToken);
                    if (string.Equals(
                            payload.Entry.SemanticDigest,
                            localValue!.SemanticDigest,
                            StringComparison.Ordinal))
                    {
                        merged[key] = payload.Entry;
                        skipped++;
                    }
                    else if (ParseDate(remoteValue.UpdatedAt)
                             > ParseDate(localValue!.UpdatedAt))
                    {
                        await conversations.UpsertAsync(payload.Detail, cancellationToken);
                        merged[key] = payload.Entry;
                        downloaded++;
                    }
                    else
                    {
                        merged[key] = await UploadAsync(
                            conversationRoot, key,
                            ensuredAgents, username, password, cancellationToken);
                        uploaded++;
                    }
                }
                else if (ParseDate(remoteValue.UpdatedAt)
                         > ParseDate(localValue!.UpdatedAt))
                {
                    merged[key] = await DownloadAsync(
                        root, remoteValue, username, password, cancellationToken);
                    downloaded++;
                }
                else
                {
                    // Same timestamp plus a different semantic digest is a real
                    // conflict, not a serializer difference: retain the prior
                    // local-wins policy.
                    merged[key] = await UploadAsync(
                        conversationRoot, key,
                        ensuredAgents, username, password, cancellationToken);
                    uploaded++;
                }
            }
            progress?.Report(new SyncProgress(
                "conversations", uploaded, downloaded, skipped, false,
                $"正在同步 {key.Agent} · {key.Id}", key.Agent, key.Id));
        }

        var entries = merged.Values
            .OrderBy(value => value.Agent, StringComparer.Ordinal)
            .ThenBy(value => value.Id, StringComparer.Ordinal)
            .ToArray();
        var remoteEntries = remoteManifest.Conversations
            .OrderBy(value => value.Agent, StringComparer.Ordinal)
            .ThenBy(value => value.Id, StringComparer.Ordinal)
            .ToArray();
        var manifestChanged = remoteManifest.SchemaVersion < 2
            || !entries.SequenceEqual(remoteEntries);
        if (manifestChanged)
        {
            var manifest = new WebDavManifest(
                2, DateTimeOffset.UtcNow.ToString("O"), entries);
            await PutAsync(
                new Uri(root, "manifest.json"),
                JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions),
                username, password, cancellationToken);
        }
        var result = new SyncProgress(
            "complete", uploaded, downloaded, skipped, true,
            $"同步完成：上传 {uploaded}，下载 {downloaded}，跳过 {skipped}。");
        progress?.Report(result);
        return result;
    }

    public static Uri BuildCollectionUri(
        SyncSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.WebdavHost))
        {
            throw new InvalidOperationException("WebDAV 服务器不能为空。");
        }
        var segments = new[]
        {
            settings.WebdavPath,
            string.IsNullOrWhiteSpace(settings.RemotePath)
                ? "chatmem"
                : settings.RemotePath,
        }
        .Select(value => value.Trim('/'))
        .Where(value => value.Length > 0);
        var path = string.Join("/", segments);
        return new UriBuilder(
            settings.WebdavScheme,
            settings.WebdavHost)
        {
            Path = path + "/",
        }.Uri;
    }

    private static HttpRequestMessage Request(
        HttpMethod method,
        Uri uri,
        string? username,
        string? password)
    {
        var request = new HttpRequestMessage(method, uri);
        if (!string.IsNullOrWhiteSpace(username))
        {
            var raw = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{username}:{password ?? ""}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", raw);
        }
        return request;
    }

    private async Task EnsureCollectionAsync(
        Uri uri,
        string? username,
        string? password,
        CancellationToken cancellationToken)
    {
        using var probe = Request(
            new HttpMethod("PROPFIND"), uri, username, password);
        probe.Headers.Add("Depth", "0");
        using var response = await _client.SendAsync(probe, cancellationToken);
        if (response.IsSuccessStatusCode) return;
        if (response.StatusCode != System.Net.HttpStatusCode.NotFound)
        {
            response.EnsureSuccessStatusCode();
        }
        using var create = Request(
            new HttpMethod("MKCOL"), uri, username, password);
        using var created = await _client.SendAsync(create, cancellationToken);
        if (!created.IsSuccessStatusCode
            && created.StatusCode != System.Net.HttpStatusCode.MethodNotAllowed)
        {
            created.EnsureSuccessStatusCode();
        }
    }

    private async Task<WebDavManifest> LoadManifestAsync(
        Uri root,
        string? username,
        string? password,
        CancellationToken cancellationToken)
    {
        using var request = Request(
            HttpMethod.Get, new Uri(root, "manifest.json"), username, password);
        using var response = await _client.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return new WebDavManifest(0, "", []);
        }
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(
            cancellationToken);
        return await JsonSerializer.DeserializeAsync<WebDavManifest>(
            stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("WebDAV manifest 无法解析。");
    }

    private async Task<WebDavManifestEntry> UploadAsync(
        Uri conversationRoot,
        (string Agent, string Id) key,
        ISet<string> ensuredAgents,
        string? username,
        string? password,
        CancellationToken cancellationToken)
    {
        var detail = await conversations!.ExportAsync(
            key.Id, cancellationToken);
        var data = JsonSerializer.SerializeToUtf8Bytes(detail, JsonOptions);
        var agentRoot = new Uri(conversationRoot, Uri.EscapeDataString(key.Agent) + "/");
        if (ensuredAgents.Add(key.Agent))
        {
            await EnsureCollectionAsync(
                agentRoot, username, password, cancellationToken);
        }
        var fileName = Base64Url(key.Id) + ".json";
        await PutAsync(
            new Uri(agentRoot, fileName),
            data,
            username, password, cancellationToken);
        return new WebDavManifestEntry(
            key.Agent, key.Id,
            $"conversations/{key.Agent}/{fileName}",
            detail.UpdatedAt,
            Hash(data),
            SemanticDigest(detail));
    }

    private async Task<WebDavManifestEntry> DownloadAsync(
        Uri root,
        WebDavManifestEntry entry,
        string? username,
        string? password,
        CancellationToken cancellationToken,
        bool persist = true)
    {
        var payload = await ReadRemotePayloadAsync(
            root, entry, username, password, cancellationToken);
        if (persist)
        {
            await conversations!.UpsertAsync(payload.Detail, cancellationToken);
        }
        return payload.Entry;
    }

    private async Task<(WebDavConversationDetail Detail, WebDavManifestEntry Entry)>
        ReadRemotePayloadAsync(
            Uri root,
            WebDavManifestEntry entry,
            string? username,
            string? password,
            CancellationToken cancellationToken)
    {
        using var request = Request(
            HttpMethod.Get, new Uri(root, entry.File), username, password);
        using var response = await _client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var data = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var detail = JsonSerializer.Deserialize<WebDavConversationDetail>(
            data, JsonOptions)
            ?? throw new InvalidDataException($"远程对话 {entry.Id} 无法解析。");
        return (
            detail,
            entry with
            {
                Sha256 = Hash(data),
                SemanticDigest = SemanticDigest(detail),
            });
    }

    private async Task PutAsync(
        Uri uri,
        byte[] data,
        string? username,
        string? password,
        CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Put, uri, username, password);
        request.Content = new ByteArrayContent(data);
        request.Content.Headers.ContentType =
            new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        using var response = await _client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private const string SemanticDigestPrefix = "aimemory-conversation-v1:";
    private static readonly byte[] SemanticDigestMagic =
        Encoding.UTF8.GetBytes("aimemory-conversation-semantic-v1\0");

    /// Shared with NativeWebDAVService.semanticDigest on macOS. The binary
    /// framing is independent of JSON text formatting and only includes fields
    /// the two local stores preserve across a sync round-trip.
    public static string SemanticDigest(WebDavConversationDetail detail)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var writer = new SemanticDigestWriter(hash);
        writer.WriteString(detail.Id);
        writer.WriteString(detail.SourceAgent);
        writer.WriteString(detail.ProjectDir);
        writer.WriteString(detail.CreatedAt);
        writer.WriteString(detail.UpdatedAt);
        writer.WritePersistentOptionalString(detail.Summary);
        writer.WritePersistentOptionalString(detail.StoragePath);

        writer.WriteArrayCount(detail.Messages.Count);
        foreach (var message in detail.Messages)
        {
            writer.WriteString(message.Id);
            writer.WriteString(message.Timestamp);
            writer.WriteString(message.Role);
            writer.WriteString(message.Content);
            writer.WriteArrayCount(message.ToolCalls.Count);
            foreach (var tool in message.ToolCalls)
            {
                writer.WriteString(tool.Id);
                writer.WriteString(tool.Name);
                writer.WriteJson(tool.Input);
                writer.WritePersistentOptionalString(tool.Output);
                writer.WriteString(tool.Status);
            }
        }

        writer.WriteArrayCount(detail.FileChanges.Count);
        foreach (var change in detail.FileChanges)
        {
            writer.WriteString(change.Path);
            writer.WriteString(change.ChangeType);
            writer.WriteString(change.Timestamp);
            writer.WritePersistentOptionalString(change.MessageId);
        }
        return SemanticDigestPrefix
            + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static bool IsCurrentSemanticDigest(string? value) =>
        value?.StartsWith(SemanticDigestPrefix, StringComparison.Ordinal) == true;

    private sealed class SemanticDigestWriter
    {
        private const byte Null = 0;
        private const byte False = 1;
        private const byte True = 2;
        private const byte Number = 3;
        private const byte String = 4;
        private const byte Array = 5;
        private const byte Object = 6;
        private readonly IncrementalHash _hash;

        public SemanticDigestWriter(IncrementalHash hash)
        {
            _hash = hash;
            _hash.AppendData(SemanticDigestMagic);
        }

        public void WritePersistentOptionalString(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                WriteNull();
                return;
            }
            WriteString(value);
        }

        public void WriteString(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            WriteTag(String);
            WriteUInt64((ulong)bytes.Length);
            _hash.AppendData(bytes);
        }

        public void WriteArrayCount(int count)
        {
            WriteTag(Array);
            WriteUInt64((ulong)count);
        }

        public void WriteJson(JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    WriteNull();
                    break;
                case JsonValueKind.False:
                    WriteTag(False);
                    break;
                case JsonValueKind.True:
                    WriteTag(True);
                    break;
                case JsonValueKind.Number:
                    WriteTag(Number);
                    var number = value.GetDouble();
                    WriteUInt64(number == 0
                        ? 0
                        : unchecked((ulong)BitConverter.DoubleToInt64Bits(number)));
                    break;
                case JsonValueKind.String:
                    WriteString(value.GetString() ?? string.Empty);
                    break;
                case JsonValueKind.Array:
                    var elements = value.EnumerateArray().ToArray();
                    WriteArrayCount(elements.Length);
                    foreach (var element in elements) WriteJson(element);
                    break;
                case JsonValueKind.Object:
                    var properties = value.EnumerateObject().ToList();
                    properties.Sort((left, right) => CompareUtf8(left.Name, right.Name));
                    WriteTag(Object);
                    WriteUInt64((ulong)properties.Count);
                    foreach (var property in properties)
                    {
                        WriteString(property.Name);
                        WriteJson(property.Value);
                    }
                    break;
                default:
                    WriteNull();
                    break;
            }
        }

        private void WriteNull() => WriteTag(Null);

        private void WriteTag(byte value) => _hash.AppendData(new[] { value });

        private void WriteUInt64(ulong value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(ulong)];
            BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
            _hash.AppendData(bytes);
        }
    }

    private static int CompareUtf8(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        var sharedLength = Math.Min(leftBytes.Length, rightBytes.Length);
        for (var index = 0; index < sharedLength; index++)
        {
            var comparison = leftBytes[index].CompareTo(rightBytes[index]);
            if (comparison != 0) return comparison;
        }
        return leftBytes.Length.CompareTo(rightBytes.Length);
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
