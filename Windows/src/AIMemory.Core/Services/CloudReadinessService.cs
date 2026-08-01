// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
namespace AIMemory.Core.Services;

public sealed record CloudReadinessResult(
    bool FolderExists,
    bool IsQuiet,
    bool HasLockFiles,
    string RecommendedAction);

public sealed class CloudReadinessService
{
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(3);
    private static readonly string[] ExactLockNames =
    [
        ".odrive",
        ".sync",
        ".tmp.driveupload",
    ];
    private static readonly string[] LockSuffixes =
    [
        ".tmp",
        ".partial",
        ".gdoc_tmp",
    ];

    public CloudReadinessResult Check(
        string folder,
        DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(folder)
            || !Directory.Exists(folder))
        {
            return new CloudReadinessResult(
                false,
                true,
                false,
                "folder_missing");
        }

        var hasLocks = HasActiveLockFiles(folder);
        var modified = Directory.GetLastWriteTimeUtc(folder);
        var recentlyModified =
            (now ?? DateTimeOffset.UtcNow).UtcDateTime - modified
            < QuietPeriod;
        var quiet = !hasLocks && !recentlyModified;
        return new CloudReadinessResult(
            true,
            quiet,
            hasLocks,
            quiet ? "safe_to_sync" : "wait");
    }

    private static bool HasActiveLockFiles(string root)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        foreach (var path in Directory.EnumerateFileSystemEntries(
                     root,
                     "*",
                     options))
        {
            var name = Path.GetFileName(path);
            if (ExactLockNames.Contains(
                    name,
                    StringComparer.OrdinalIgnoreCase)
                || name.StartsWith("~$", StringComparison.Ordinal)
                || LockSuffixes.Any(suffix =>
                    name.EndsWith(
                        suffix,
                        StringComparison.OrdinalIgnoreCase))
                || name.Contains(
                    ".crswap",
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
