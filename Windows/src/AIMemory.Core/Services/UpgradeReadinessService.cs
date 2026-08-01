// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
using AIMemory.Core.Persistence;

namespace AIMemory.Core.Services;

public sealed record UpgradeReadinessCheck(
    string Key,
    string Status,
    string DetailCode,
    string? DetailArgument = null);

public sealed record UpgradeReadinessReport(
    string Status,
    IReadOnlyList<UpgradeReadinessCheck> Checks)
{
    public int ErrorCount =>
        Checks.Count(value => value.Status == "error");

    public int WarningCount =>
        Checks.Count(value => value.Status == "warning");
}

public sealed class UpgradeReadinessService(
    AIMemoryDatabase database,
    SettingsStore settings,
    string? settingsPath = null)
{
    private readonly string _settingsPath =
        settingsPath ?? DataPaths.SettingsPath;

    public async Task<UpgradeReadinessReport> CheckAsync(
        Func<string, bool> passwordExists,
        CancellationToken cancellationToken = default)
    {
        var checks = new List<UpgradeReadinessCheck>();
        AIMemory.Core.Models.AppSettings? currentSettings;
        try
        {
            currentSettings = await settings.LoadAsync(cancellationToken);
            checks.Add(new UpgradeReadinessCheck(
                "settings",
                "ok",
                File.Exists(_settingsPath)
                    ? "settings_parsed"
                    : "settings_defaults"));
        }
        catch (Exception exception)
        {
            currentSettings = null;
            checks.Add(new UpgradeReadinessCheck(
                "settings",
                "error",
                "settings_invalid",
                exception.Message));
        }

        AddWebDavChecks(checks, currentSettings, passwordExists);
        await AddDatabaseCheckAsync(checks, cancellationToken);

        var status = checks.Any(value => value.Status == "error")
            ? "error"
            : checks.Any(value => value.Status == "warning")
                ? "warning"
                : "ok";
        return new UpgradeReadinessReport(status, checks);
    }

    private static void AddWebDavChecks(
        ICollection<UpgradeReadinessCheck> checks,
        AIMemory.Core.Models.AppSettings? settings,
        Func<string, bool> passwordExists)
    {
        if (settings is null
            || !string.Equals(
                settings.Sync.Provider,
                "webdav",
                StringComparison.OrdinalIgnoreCase))
        {
            checks.Add(new UpgradeReadinessCheck(
                "webdav_profile",
                "ok",
                "webdav_disabled"));
            checks.Add(new UpgradeReadinessCheck(
                "webdav_password",
                "ok",
                "password_not_required"));
            return;
        }

        var sync = settings.Sync;
        var complete =
            !string.IsNullOrWhiteSpace(sync.WebdavHost)
            && !string.IsNullOrWhiteSpace(sync.Username)
            && !string.IsNullOrWhiteSpace(sync.RemotePath);
        checks.Add(new UpgradeReadinessCheck(
            "webdav_profile",
            complete ? "ok" : "warning",
            complete ? "webdav_complete" : "webdav_incomplete"));

        try
        {
            var present =
                !string.IsNullOrWhiteSpace(sync.Username)
                && passwordExists(sync.Username);
            checks.Add(new UpgradeReadinessCheck(
                "webdav_password",
                present ? "ok" : "warning",
                present ? "password_present" : "password_missing"));
        }
        catch (Exception exception)
        {
            checks.Add(new UpgradeReadinessCheck(
                "webdav_password",
                "warning",
                "password_unavailable",
                exception.Message));
        }
    }

    private async Task AddDatabaseCheckAsync(
        ICollection<UpgradeReadinessCheck> checks,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = database.OpenConnection();
            var versionCommand = connection.CreateCommand();
            versionCommand.CommandText = "PRAGMA user_version;";
            var version = Convert.ToInt32(
                await versionCommand.ExecuteScalarAsync(cancellationToken));
            if (version != AIMemoryDatabase.SchemaVersion)
            {
                throw new InvalidOperationException(
                    $"schema {version}, expected {AIMemoryDatabase.SchemaVersion}");
            }

            var checkCommand = connection.CreateCommand();
            checkCommand.CommandText = "PRAGMA quick_check;";
            var quickCheck = Convert.ToString(
                await checkCommand.ExecuteScalarAsync(cancellationToken));
            if (!string.Equals(
                    quickCheck,
                    "ok",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    quickCheck ?? "quick_check returned no result");
            }

            checks.Add(new UpgradeReadinessCheck(
                "memory_store",
                "ok",
                "database_valid",
                version.ToString()));
        }
        catch (Exception exception)
        {
            checks.Add(new UpgradeReadinessCheck(
                "memory_store",
                "error",
                "database_invalid",
                exception.Message));
        }
    }
}
