using System.Text.Json.Serialization;

namespace AIMemory.Core.Models;

public sealed class AppSettings
{
    public int SettingsVersion { get; set; } = 1;
    public string Language { get; set; } = "zh-Hans";
    public string FontFamily { get; set; } = "Segoe UI Variable";
    public int TrashRetentionDays { get; set; } = 14;
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
        SettingsVersion = Math.Max(1, SettingsVersion);
        TrashRetentionDays = Math.Clamp(TrashRetentionDays, 1, 365);
        AutoBackupIntervalMinutes = Math.Clamp(
            AutoBackupIntervalMinutes, 5, 1_440);
        UpdateFeedUrl = UpdateFeedUrl?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(UpdateFeedUrl))
        {
            UpdateFeedUrl =
                "https://api.github.com/repos/douxy1994/AI-Memory/releases/latest";
        }
        Sync ??= new();
        FavoriteConversations ??= [];
        MachineGroupNames ??= [];
        MachineGroupOverrides ??= [];
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
}
