// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
namespace AIMemory.Core.Persistence;

public static class DataPaths
{
    public static string SupportDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AIMemory");

    public static string DatabasePath => Path.Combine(SupportDirectory, "aimemory.db");
    public static string SettingsPath => Path.Combine(SupportDirectory, "settings.json");
    public static string TrashDirectory => Path.Combine(SupportDirectory, "trash");
    public static string BackupDirectory => Path.Combine(SupportDirectory, "backups");
    public static string UpdateDirectory => Path.Combine(SupportDirectory, "updates");

    public static IReadOnlyList<string> ChatMemDatabaseCandidates =>
    [
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ChatMem", "chatmem.db"),
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ChatMem", "chatmem.db"),
    ];

    public static IReadOnlyList<string> ChatMemSettingsCandidates =>
    [
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ChatMem", "settings.json"),
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ChatMem", "settings.json"),
    ];

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(SupportDirectory);
        Directory.CreateDirectory(TrashDirectory);
        Directory.CreateDirectory(BackupDirectory);
        Directory.CreateDirectory(UpdateDirectory);
    }
}
