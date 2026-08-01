// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
using System.Text.Json;
using AIMemory.Core.Models;
using AIMemory.Core.Persistence;

namespace AIMemory.Core.Services;

public sealed record ChatMemWebDavImportResult(
    string? SourcePath,
    bool SettingsImported,
    bool CredentialImported,
    bool MissingUsername,
    bool MissingCredential,
    string? SkippedReason = null)
{
    public bool Changed => SettingsImported || CredentialImported;
    public bool NeedsAttention => MissingUsername || MissingCredential;
}

/// <summary>
/// Read-only, idempotent migration of ChatMem's WebDAV profile into AI Memory.
/// Credential access is injected so the Core project never depends on a
/// platform-specific secret store.
/// </summary>
public sealed class ChatMemWebDavImportService(
    SettingsStore? settingsStore = null,
    IReadOnlyList<string>? sourceCandidates = null)
{
    private readonly SettingsStore _settingsStore =
        settingsStore ?? new SettingsStore();
    private readonly IReadOnlyList<string> _sourceCandidates =
        sourceCandidates ?? DataPaths.ChatMemSettingsCandidates;

    public async Task<ChatMemWebDavImportResult> ImportAsync(
        Func<string, string?> loadCurrentCredential,
        Func<string, string?> loadLegacyCredential,
        Action<string, string> saveCredential,
        CancellationToken cancellationToken = default)
    {
        var sourcePath = _sourceCandidates.FirstOrDefault(File.Exists);
        if (sourcePath is null)
        {
            return new ChatMemWebDavImportResult(
                null, false, false, false, false, "source_not_found");
        }

        var sourceData = await File.ReadAllBytesAsync(
            sourcePath, cancellationToken);
        var source = ParseSource(sourceData);
        if (string.IsNullOrWhiteSpace(source.Host))
        {
            return new ChatMemWebDavImportResult(
                sourcePath, false, false, false, false, "host_missing");
        }

        var target = await _settingsStore.LoadAsync(cancellationToken);
        var targetHost = target.Sync.WebdavHost.Trim();
        var sourceHost = source.Host.Trim();
        var sourceUsername = source.Username.Trim();
        var endpointNeedsImport = targetHost.Length == 0;
        var endpointMatches =
            string.Equals(targetHost, sourceHost, StringComparison.Ordinal)
            && string.Equals(
                target.Sync.Username.Trim(),
                sourceUsername,
                StringComparison.Ordinal);
        if (!endpointNeedsImport && !endpointMatches)
        {
            return new ChatMemWebDavImportResult(
                sourcePath, false, false, false, false,
                "different_endpoint_configured");
        }

        if (endpointNeedsImport)
        {
            target.Sync.Provider = "webdav";
            target.Sync.WebdavScheme = source.Scheme;
            target.Sync.WebdavHost = source.Host;
            target.Sync.WebdavPath = source.Path;
            target.Sync.Username = source.Username;
            target.Sync.RemotePath = source.RemotePath;
            target.Sync.DownloadMode = source.DownloadMode;
            await _settingsStore.SaveAsync(target, cancellationToken);
        }

        if (sourceUsername.Length == 0)
        {
            return new ChatMemWebDavImportResult(
                sourcePath,
                endpointNeedsImport,
                false,
                true,
                false);
        }

        if (!string.IsNullOrEmpty(loadCurrentCredential(sourceUsername)))
        {
            return new ChatMemWebDavImportResult(
                sourcePath,
                endpointNeedsImport,
                false,
                false,
                false);
        }

        var password = loadLegacyCredential(sourceUsername);
        if (string.IsNullOrEmpty(password))
        {
            password = source.Password;
        }
        if (string.IsNullOrEmpty(password))
        {
            return new ChatMemWebDavImportResult(
                sourcePath,
                endpointNeedsImport,
                false,
                false,
                true);
        }

        saveCredential(sourceUsername, password);
        return new ChatMemWebDavImportResult(
            sourcePath,
            endpointNeedsImport,
            true,
            false,
            false);
    }

    private static SourceWebDavSettings ParseSource(byte[] data)
    {
        using var document = JsonDocument.Parse(
            data,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !TryGetProperty(document.RootElement, "sync", out var sync)
            || sync.ValueKind != JsonValueKind.Object)
        {
            return SourceWebDavSettings.Empty;
        }

        var legacyUrl = GetString(sync, "webdavUrl", "webdav_url");
        var parsedUrl = ParseLegacyUrl(legacyUrl);
        var scheme = GetString(sync, "webdavScheme", "webdav_scheme");
        if (scheme is not ("http" or "https"))
        {
            scheme = parsedUrl.Scheme;
        }
        var host = GetString(sync, "webdavHost", "webdav_host");
        if (string.IsNullOrWhiteSpace(host)) host = parsedUrl.Host;
        var path = GetString(sync, "webdavPath", "webdav_path");
        if (string.IsNullOrWhiteSpace(path)) path = parsedUrl.Path;
        var remotePath = GetString(sync, "remotePath", "remote_path");
        if (string.IsNullOrWhiteSpace(remotePath)) remotePath = "chatmem";
        var downloadMode = GetString(sync, "downloadMode", "download_mode");
        downloadMode = downloadMode == "as-needed" ? "as-needed" : "on-sync";

        return new SourceWebDavSettings(
            scheme,
            host,
            path,
            GetString(sync, "username", "webdav_username"),
            remotePath,
            downloadMode,
            GetString(sync, "password"));
    }

    private static SourceWebDavSettings ParseLegacyUrl(string value)
    {
        if (Uri.TryCreate(value.Trim(), UriKind.Absolute, out var url)
            && url.Scheme is "http" or "https")
        {
            return new SourceWebDavSettings(
                url.Scheme,
                url.Authority,
                url.AbsolutePath.Trim('/'),
                "",
                "chatmem",
                "on-sync",
                "");
        }
        return SourceWebDavSettings.Empty;
    }

    private static string GetString(
        JsonElement value,
        params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetProperty(value, name, out var property)
                && property.ValueKind == JsonValueKind.String)
            {
                return property.GetString()?.Trim() ?? "";
            }
        }
        return "";
    }

    private static bool TryGetProperty(
        JsonElement value,
        string name,
        out JsonElement property)
    {
        if (value.TryGetProperty(name, out property)) return true;
        foreach (var candidate in value.EnumerateObject())
        {
            if (string.Equals(
                    candidate.Name,
                    name,
                    StringComparison.OrdinalIgnoreCase))
            {
                property = candidate.Value;
                return true;
            }
        }
        property = default;
        return false;
    }

    private sealed record SourceWebDavSettings(
        string Scheme,
        string Host,
        string Path,
        string Username,
        string RemotePath,
        string DownloadMode,
        string Password)
    {
        public static SourceWebDavSettings Empty { get; } =
            new("https", "", "", "", "chatmem", "on-sync", "");
    }
}
