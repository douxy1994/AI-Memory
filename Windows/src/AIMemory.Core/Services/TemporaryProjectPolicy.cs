// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
using Microsoft.Data.Sqlite;

namespace AIMemory.Core.Services;

/// <summary>Classifies project directories, never history storage locations.</summary>
public static class TemporaryProjectPolicy
{
    private static readonly string[] PosixRoots =
        ["/tmp", "/private/tmp", "/var/tmp", "/private/var/tmp", "/var/folders", "/private/var/folders"];

    public static bool IsTemporaryProject(string? path) =>
        IsTemporaryProject(path, LocalTemporaryRoots());

    public static bool IsTemporaryProject(string? path, IEnumerable<string> temporaryRoots)
    {
        var project = Normalize(path);
        if (project is null) return false;
        return PosixRoots.Concat(temporaryRoots).Any(root =>
        {
            var candidate = Normalize(root);
            if (candidate is null || candidate.Value.Windows != project.Value.Windows) return false;
            var comparison = project.Value.Windows
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return project.Value.Path.Equals(candidate.Value.Path, comparison)
                || project.Value.Path.StartsWith(candidate.Value.Path.TrimEnd('/') + "/", comparison);
        });
    }

    internal static void RegisterQueryFunction(SqliteConnection connection) =>
        connection.CreateFunction<string?, bool>("is_temporary_project", IsTemporaryProject);

    private static IEnumerable<string> LocalTemporaryRoots()
    {
        yield return Path.GetTempPath();
        foreach (var name in new[] { "TEMP", "TMP" })
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } root) yield return root;
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrEmpty(windows)) yield return Path.Combine(windows, "Temp");
    }

    private static (string Path, bool Windows)? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var path = value.Trim().Replace('\\', '/');
        if (path.StartsWith("//?/UNC/", StringComparison.OrdinalIgnoreCase)) path = "//" + path[8..];
        else if (path.StartsWith("//?/", StringComparison.Ordinal)) path = path[4..];
        var drive = path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '/';
        var unc = path.StartsWith("//", StringComparison.Ordinal);
        if (!drive && !unc && !path.StartsWith('/')) return null;
        var prefix = drive ? path[..2] : unc ? "//" : "";
        var components = (drive ? path[3..] : path).Split('/', StringSplitOptions.RemoveEmptyEntries);
        var parts = new List<string>();
        var floor = unc ? 2 : 0; // A UNC server/share is a volume, not traversable parents.
        foreach (var part in components)
        {
            if (part == ".") continue;
            if (part == "..") { if (parts.Count > floor) parts.RemoveAt(parts.Count - 1); }
            else parts.Add(part);
        }
        if (unc && parts.Count < 2) return null;
        return (prefix + (unc ? "" : "/") + string.Join('/', parts), drive || unc);
    }
}
