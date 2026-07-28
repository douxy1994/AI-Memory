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
            (WebDavConversationDetail Detail, byte[] Data, string Hash)>();
        foreach (var summary in await conversations.ListAsync(
                     limit: 5_000,
                     cancellationToken: cancellationToken))
        {
            var detail = await conversations.ExportAsync(
                summary.Id, cancellationToken);
            var data = JsonSerializer.SerializeToUtf8Bytes(detail, JsonOptions);
            local[(detail.SourceAgent, detail.Id)] =
                (detail, data, Hash(data));
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
            local.TryGetValue(key, out var localValue);
            remote.TryGetValue(key, out var remoteValue);
            if (localValue.Detail is not null && remoteValue is null)
            {
                merged[key] = await UploadAsync(
                    conversationRoot, key, localValue,
                    ensuredAgents, username, password, cancellationToken);
                uploaded++;
            }
            else if (localValue.Detail is null && remoteValue is not null)
            {
                merged[key] = await DownloadAsync(
                    root, remoteValue, username, password, cancellationToken);
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
                    merged[key] = await DownloadAsync(
                        root, remoteValue, username, password, cancellationToken);
                    downloaded++;
                }
                else
                {
                    merged[key] = await UploadAsync(
                        conversationRoot, key, localValue,
                        ensuredAgents, username, password, cancellationToken);
                    uploaded++;
                }
            }
            progress?.Report(new SyncProgress(
                "conversations", uploaded, downloaded, skipped, false,
                $"正在同步 {key.Agent} · {key.Id}"));
        }

        var entries = merged.Values
            .OrderBy(value => value.Agent, StringComparer.Ordinal)
            .ThenBy(value => value.Id, StringComparer.Ordinal)
            .ToArray();
        var manifest = new WebDavManifest(
            2, DateTimeOffset.UtcNow.ToString("O"), entries);
        await PutAsync(
            new Uri(root, "manifest.json"),
            JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions),
            username, password, cancellationToken);
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
        (WebDavConversationDetail Detail, byte[] Data, string Hash) payload,
        ISet<string> ensuredAgents,
        string? username,
        string? password,
        CancellationToken cancellationToken)
    {
        var agentRoot = new Uri(conversationRoot, Uri.EscapeDataString(key.Agent) + "/");
        if (ensuredAgents.Add(key.Agent))
        {
            await EnsureCollectionAsync(
                agentRoot, username, password, cancellationToken);
        }
        var fileName = Base64Url(key.Id) + ".json";
        await PutAsync(
            new Uri(agentRoot, fileName),
            payload.Data,
            username, password, cancellationToken);
        return new WebDavManifestEntry(
            key.Agent, key.Id,
            $"conversations/{key.Agent}/{fileName}",
            payload.Detail.UpdatedAt,
            payload.Hash);
    }

    private async Task<WebDavManifestEntry> DownloadAsync(
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
        await conversations!.UpsertAsync(detail, cancellationToken);
        return entry with { Sha256 = Hash(data) };
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
