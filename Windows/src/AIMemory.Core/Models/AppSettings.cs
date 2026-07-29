using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIMemory.Core.Models;

public sealed class AppSettings
{
    public int SettingsVersion { get; set; } = 1;
    public string Language { get; set; } = "system";
    public string FontFamily { get; set; } = "system";
    public int TrashRetentionDays { get; set; } = 14;
    public bool AutoCaptureMemory { get; set; } = true;
    public bool AutoBackupEnabled { get; set; }
    public int AutoBackupIntervalMinutes { get; set; } = 30;
    public bool AutoCheckUpdates { get; set; } = true;
    public string UpdateFeedUrl { get; set; } =
        "https://api.github.com/repos/douxy1994/AI-Memory/releases/latest";
    public SyncSettings Sync { get; set; } = new();
    public Dictionary<string, FavoriteConversationSnapshot> FavoriteConversations { get; set; } = [];
    public Dictionary<string, string> MachineGroupNames { get; set; } = [];
    public Dictionary<string, string> MachineGroupOverrides { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? ExtensionData { get; set; }

    public void Normalize()
    {
        if (ExtensionData is not null)
        {
            if (ExtensionData.TryGetValue(
                    "schemaVersion",
                    out var schemaVersion)
                && schemaVersion.ValueKind == JsonValueKind.Number
                && schemaVersion.TryGetInt32(out var parsedVersion))
            {
                SettingsVersion = parsedVersion;
                ExtensionData.Remove("schemaVersion");
            }
            if (ExtensionData.TryGetValue("locale", out var locale)
                && locale.ValueKind == JsonValueKind.String)
            {
                Language = locale.GetString() ?? Language;
                ExtensionData.Remove("locale");
            }
            if (ExtensionData.TryGetValue(
                    "font_family",
                    out var legacyFont)
                && legacyFont.ValueKind == JsonValueKind.String)
            {
                FontFamily = legacyFont.GetString() ?? FontFamily;
                ExtensionData.Remove("font_family");
            }
            ApplyLegacyBoolean(
                ExtensionData,
                "auto_check_updates",
                value => AutoCheckUpdates = value);
            ApplyLegacyBoolean(
                ExtensionData,
                "auto_capture_memory",
                value => AutoCaptureMemory = value);
            ApplyLegacyBoolean(
                ExtensionData,
                "auto_backup_enabled",
                value => AutoBackupEnabled = value);
            ApplyLegacyInteger(
                ExtensionData,
                "trash_retention_days",
                value => TrashRetentionDays = value);
            ApplyLegacyInteger(
                ExtensionData,
                "auto_backup_interval_minutes",
                value => AutoBackupIntervalMinutes = value);
            if (ExtensionData.TryGetValue(
                    "update_feed_url",
                    out var legacyUpdateFeed)
                && legacyUpdateFeed.ValueKind == JsonValueKind.String)
            {
                UpdateFeedUrl =
                    legacyUpdateFeed.GetString() ?? UpdateFeedUrl;
                ExtensionData.Remove("update_feed_url");
            }
            if (ExtensionData.Count == 0) ExtensionData = null;
        }
        SettingsVersion = Math.Max(1, SettingsVersion);
        TrashRetentionDays = Math.Clamp(TrashRetentionDays, 1, 365);
        AutoBackupIntervalMinutes = Math.Clamp(
            AutoBackupIntervalMinutes, 5, 1_440);
        Language = Services.LanguagePreferenceService.NormalizeId(Language);
        FontFamily = Services.FontPreferenceService.NormalizeId(FontFamily);
        UpdateFeedUrl = UpdateFeedUrl?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(UpdateFeedUrl))
        {
            UpdateFeedUrl =
                "https://api.github.com/repos/douxy1994/AI-Memory/releases/latest";
        }
        Sync ??= new();
        Sync.Normalize();
        FavoriteConversations ??= [];
        MachineGroupNames ??= [];
        MachineGroupOverrides ??= [];
    }

    private static void ApplyLegacyBoolean(
        Dictionary<string, JsonElement> values,
        string key,
        Action<bool> apply)
    {
        if (!values.TryGetValue(key, out var value)
            || value.ValueKind is not (
                JsonValueKind.True or JsonValueKind.False))
        {
            return;
        }
        apply(value.GetBoolean());
        values.Remove(key);
    }

    private static void ApplyLegacyInteger(
        Dictionary<string, JsonElement> values,
        string key,
        Action<int> apply)
    {
        if (!values.TryGetValue(key, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var parsed))
        {
            return;
        }
        apply(parsed);
        values.Remove(key);
    }
}

public sealed class SyncSettings
{
    public string Provider { get; set; } = "none";
    public string WebdavScheme { get; set; } = "https";
    public string WebdavHost { get; set; } = "";
    public string WebdavPath { get; set; } = "";
    public string Username { get; set; } = "";
    public string RemotePath { get; set; } = "chatmem";
    public string DownloadMode { get; set; } = "merge";
    public string SyncFolder { get; set; } = "";

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    public void Normalize()
    {
        if (ExtensionData is null) return;
        ApplyLegacyString("webdav_scheme", value => WebdavScheme = value);
        ApplyLegacyString("webdav_host", value => WebdavHost = value);
        ApplyLegacyString("webdav_path", value => WebdavPath = value);
        ApplyLegacyString("webdav_username", value => Username = value);
        ApplyLegacyString("remote_path", value => RemotePath = value);
        ApplyLegacyString("download_mode", value => DownloadMode = value);
        ApplyLegacyString("sync_folder", value => SyncFolder = value);
        if (ExtensionData.Count == 0) ExtensionData = null;
    }

    private void ApplyLegacyString(string key, Action<string> apply)
    {
        if (ExtensionData is null
            || !ExtensionData.TryGetValue(key, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return;
        }
        apply(value.GetString() ?? "");
        ExtensionData.Remove(key);
    }
}
