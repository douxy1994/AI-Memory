using System.Net;
using System.Text.Json;

namespace AIMemory.Core.Services;

public sealed record UpdateRelease(
    string Version,
    string Title,
    string Notes,
    Uri PageUri,
    Uri? AssetUri,
    string? AssetName);

public sealed record UpdateCheckResult(
    bool IsUpdateAvailable,
    UpdateRelease Release);

public sealed class UpdateService
{
    private readonly HttpClient _client;

    public UpdateService(HttpClient? client = null)
    {
        _client = client ?? new HttpClient();
        if (!_client.DefaultRequestHeaders.UserAgent.Any())
        {
            _client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "AI-Memory-Windows/0.1");
        }
    }

    public async Task<UpdateCheckResult> CheckAsync(
        string? feedUrl,
        string currentVersion,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(feedUrl, UriKind.Absolute, out var feedUri)
            || feedUri.Scheme is not ("https" or "http"))
        {
            throw new InvalidOperationException("尚未配置有效的 AI Memory 更新源。");
        }
        using var response = await _client.GetAsync(feedUri, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"更新服务器返回 HTTP {(int)response.StatusCode}。");
        }
        await using var stream = await response.Content.ReadAsStreamAsync(
            cancellationToken);
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken);
        var release = DecodeRelease(document.RootElement);
        return new UpdateCheckResult(
            CompareVersions(release.Version, currentVersion) > 0,
            release);
    }

    public async Task<string> DownloadAsync(
        UpdateRelease release,
        string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        if (release.AssetUri is null || string.IsNullOrWhiteSpace(release.AssetName))
        {
            throw new InvalidOperationException(
                "该版本没有可安装的 Windows MSIX、MSIXBundle 或 AppInstaller。");
        }
        Directory.CreateDirectory(destinationDirectory);
        var safeName = Path.GetFileName(release.AssetName);
        var destination = Path.Combine(
            destinationDirectory,
            $"{Path.GetFileNameWithoutExtension(safeName)}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}{Path.GetExtension(safeName)}");
        using var response = await _client.GetAsync(
            release.AssetUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"下载安装包失败（HTTP {(int)response.StatusCode}）。");
        }
        await using var source = await response.Content.ReadAsStreamAsync(
            cancellationToken);
        await using var target = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            true);
        await source.CopyToAsync(target, cancellationToken);
        return destination;
    }

    public static UpdateRelease DecodeRelease(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("tag_name", out var tagValue)
            || tagValue.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(tagValue.GetString())
            || !root.TryGetProperty("html_url", out var pageValue)
            || pageValue.ValueKind != JsonValueKind.String
            || !Uri.TryCreate(pageValue.GetString(), UriKind.Absolute, out var pageUri))
        {
            throw new InvalidOperationException("更新源返回了无法识别的版本信息。");
        }

        string? assetName = null;
        Uri? assetUri = null;
        if (root.TryGetProperty("assets", out var assets)
            && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var extension in new[] { ".msixbundle", ".appinstaller", ".msix" })
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = StringProperty(asset, "name");
                    var compact = new string((name ?? "")
                        .Where(char.IsLetterOrDigit)
                        .Select(char.ToLowerInvariant)
                        .ToArray());
                    if (string.IsNullOrWhiteSpace(name)
                        || !compact.Contains("aimemory", StringComparison.Ordinal)
                        || !name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
                        || !Uri.TryCreate(
                            StringProperty(asset, "browser_download_url"),
                            UriKind.Absolute,
                            out var candidate))
                    {
                        continue;
                    }
                    assetName = name;
                    assetUri = candidate;
                    break;
                }
                if (assetUri is not null) break;
            }
        }

        var rawVersion = tagValue.GetString()!;
        return new UpdateRelease(
            NormalizeVersion(rawVersion),
            StringProperty(root, "name") ?? rawVersion,
            StringProperty(root, "body") ?? "",
            pageUri,
            assetUri,
            assetName);
    }

    public static int CompareVersions(string candidate, string current)
    {
        var left = VersionParts(candidate);
        var right = VersionParts(current);
        for (var index = 0; index < Math.Max(left.Count, right.Count); index++)
        {
            var lhs = index < left.Count ? left[index] : 0;
            var rhs = index < right.Count ? right[index] : 0;
            if (lhs != rhs) return lhs.CompareTo(rhs);
        }
        return 0;
    }

    private static string? StringProperty(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(property, out var result)
        && result.ValueKind == JsonValueKind.String
            ? result.GetString()
            : null;

    private static string NormalizeVersion(string value) =>
        value.Trim().TrimStart('v', 'V');

    private static IReadOnlyList<int> VersionParts(string value) =>
        NormalizeVersion(value)
            .Split('.', StringSplitOptions.None)
            .Select(component =>
            {
                var numeric = new string(component.TakeWhile(char.IsDigit).ToArray());
                return int.TryParse(numeric, out var number) ? number : 0;
            })
            .ToArray();
}
