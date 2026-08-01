// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
using AIMemory.Core.Persistence;
using Microsoft.Data.Sqlite;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace AIMemory.Core.Services;

public sealed record BackupResult(
    string Path,
    bool Created,
    bool DatabaseChanged,
    bool SettingsChanged);

public sealed class BackupService(
    AIMemoryDatabase database,
    string? backupDirectory = null,
    string? settingsPath = null)
{
    private readonly string _backupDirectory =
        backupDirectory ?? DataPaths.BackupDirectory;
    private readonly string _settingsPath =
        settingsPath ?? DataPaths.SettingsPath;

    public async Task<string> CreateRecoveryPointAsync(
        CancellationToken cancellationToken = default)
        => (await CreateRecoveryPointDetailedAsync(
            "manual", 10, cancellationToken)).Path;

    public async Task<BackupResult> CreateRecoveryPointDetailedAsync(
        string reason,
        int keep = 10,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_backupDirectory);
        var previous = ListRecoveryPoints().FirstOrDefault();
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
        var candidate = Path.Combine(_backupDirectory, $"aimemory-{stamp}.db");
        if (File.Exists(candidate))
        {
            stamp += $"-{Guid.NewGuid():N}";
        }
        var destination = Path.Combine(
            _backupDirectory,
            $"aimemory-{stamp}.db");

        await using (var source = database.OpenConnection())
        await using (var target = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = destination,
                Pooling = false,
            }.ToString()))
        {
            await target.OpenAsync(cancellationToken);
            source.BackupDatabase(target);
        }
        // The source connection may have been returned to the shared SQLite
        // pool. Release it before replacing or deleting this recovery point.
        SqliteConnection.ClearAllPools();

        var settingsCopy = Path.Combine(
            _backupDirectory,
            $"settings-{stamp}.json");
        if (File.Exists(_settingsPath))
        {
            File.Copy(_settingsPath, settingsCopy, true);
        }
        try
        {
            var databaseHash = await HashAsync(
                destination, cancellationToken);
            var settingsHash = File.Exists(settingsCopy)
                ? await HashAsync(settingsCopy, cancellationToken)
                : null;
            var previousDatabaseHash = previous is null
                ? null
                : await HashAsync(previous, cancellationToken);
            var previousSettings = previous is null
                ? null
                : MatchingSettingsPath(previous);
            var previousSettingsHash =
                previousSettings is not null
                && File.Exists(previousSettings)
                    ? await HashAsync(
                        previousSettings, cancellationToken)
                    : null;
            var databaseChanged = !string.Equals(
                databaseHash,
                previousDatabaseHash,
                StringComparison.Ordinal);
            var settingsChanged = !string.Equals(
                settingsHash,
                previousSettingsHash,
                StringComparison.Ordinal);

            if (previous is not null
                && !databaseChanged
                && !settingsChanged)
            {
                DeleteFileIfExists(destination);
                DeleteFileIfExists(settingsCopy);
                return new BackupResult(
                    previous, false, false, false);
            }
            if (previous is not null && !databaseChanged)
            {
                ReplaceWithHardLinkOrCopy(previous, destination);
            }
            if (previousSettings is not null
                && File.Exists(previousSettings)
                && !settingsChanged)
            {
                ReplaceWithHardLinkOrCopy(
                    previousSettings, settingsCopy);
            }
            var manifest = new
            {
                schema_version = 2,
                created_at = DateTimeOffset.UtcNow.ToString("O"),
                reason,
                source_database = database.Path,
                database_sha256 = databaseHash,
                settings_sha256 = settingsHash,
                database_changed = databaseChanged,
                settings_changed = settingsChanged,
                storage_mode = "incremental-hardlink",
            };
            await File.WriteAllTextAsync(
                MatchingManifestPath(destination),
                JsonSerializer.Serialize(
                    manifest,
                    new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken);
            Prune(Math.Clamp(keep, 1, 100));
            return new BackupResult(
                destination,
                true,
                databaseChanged,
                settingsChanged);
        }
        catch
        {
            try
            {
                SqliteConnection.ClearAllPools();
                DeleteFileIfExists(destination);
                DeleteFileIfExists(settingsCopy);
                DeleteFileIfExists(MatchingManifestPath(destination));
            }
            catch (IOException)
            {
                // Preserve the original backup error. A uniquely named
                // incomplete recovery point can be cleaned on the next run.
            }
            throw;
        }
    }

    public IReadOnlyList<string> ListRecoveryPoints() =>
        Directory.Exists(_backupDirectory)
            ? Directory.EnumerateFiles(_backupDirectory, "aimemory-*.db")
                .OrderDescending()
                .ToArray()
            : [];

    public async Task<string> RestoreRecoveryPointAsync(
        string recoveryPoint,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(recoveryPoint))
        {
            throw new FileNotFoundException("找不到恢复点。", recoveryPoint);
        }
        if (string.Equals(
                Path.GetFullPath(recoveryPoint),
                Path.GetFullPath(database.Path),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("恢复点不能是当前数据库。");
        }

        await ValidateAsync(recoveryPoint, cancellationToken);
        var originalSettingsExisted = File.Exists(_settingsPath);
        var safetyBackup = await CreateRecoveryPointAsync(cancellationToken);
        var temporary = database.Path + $".restoring-{Guid.NewGuid():N}";
        var settingsTemporary = _settingsPath + $".restoring-{Guid.NewGuid():N}";
        var recoverySettings = MatchingSettingsPath(recoveryPoint);

        Directory.CreateDirectory(Path.GetDirectoryName(database.Path)!);
        File.Copy(recoveryPoint, temporary, true);
        if (File.Exists(recoverySettings))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            File.Copy(recoverySettings, settingsTemporary, true);
        }
        var databaseReplaced = false;
        var settingsReplaced = false;
        try
        {
            await ValidateAsync(temporary, cancellationToken);
            ReplaceDatabaseFile(temporary);
            databaseReplaced = true;
            if (File.Exists(settingsTemporary))
            {
                File.Move(settingsTemporary, _settingsPath, true);
                settingsReplaced = true;
            }
            await database.InitializeAsync(cancellationToken);
            return safetyBackup;
        }
        catch (Exception restoreError)
        {
            File.Delete(temporary);
            File.Delete(settingsTemporary);
            if (!databaseReplaced)
            {
                throw;
            }

            try
            {
                var rollback = database.Path + $".rollback-{Guid.NewGuid():N}";
                File.Copy(safetyBackup, rollback, true);
                await ValidateAsync(rollback, cancellationToken);
                ReplaceDatabaseFile(rollback);
                var safetySettings = MatchingSettingsPath(safetyBackup);
                if (settingsReplaced && File.Exists(safetySettings))
                {
                    File.Copy(safetySettings, _settingsPath, true);
                }
                else if (settingsReplaced && !originalSettingsExisted)
                {
                    File.Delete(_settingsPath);
                }
                await database.InitializeAsync(cancellationToken);
            }
            catch (Exception rollbackError)
            {
                throw new AggregateException(
                    "恢复失败，并且自动回滚当前数据库也失败。原始安全备份仍保留。",
                    restoreError,
                    rollbackError);
            }
            throw new InvalidOperationException(
                "恢复失败，已自动回滚到恢复前的数据。",
                restoreError);
        }
    }

    private void ReplaceDatabaseFile(string replacement)
    {
        SqliteConnection.ClearAllPools();
        DeleteFileIfExists(database.Path + "-wal");
        DeleteFileIfExists(database.Path + "-shm");
        File.Move(replacement, database.Path, true);
    }

    private static string MatchingSettingsPath(string recoveryPoint)
    {
        var fileName = Path.GetFileNameWithoutExtension(recoveryPoint);
        var suffix = fileName.StartsWith("aimemory-", StringComparison.Ordinal)
            ? fileName["aimemory-".Length..]
            : fileName;
        return Path.Combine(
            Path.GetDirectoryName(recoveryPoint)!,
            $"settings-{suffix}.json");
    }

    private static string MatchingManifestPath(string recoveryPoint)
    {
        var fileName = Path.GetFileNameWithoutExtension(recoveryPoint);
        var suffix = fileName.StartsWith("aimemory-", StringComparison.Ordinal)
            ? fileName["aimemory-".Length..]
            : fileName;
        return Path.Combine(
            Path.GetDirectoryName(recoveryPoint)!,
            $"manifest-{suffix}.json");
    }

    private static async Task<string> HashAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken))
            .ToLowerInvariant();
    }

    private static void ReplaceWithHardLinkOrCopy(
        string source,
        string destination)
    {
        DeleteFileIfExists(destination);
        if (OperatingSystem.IsWindows()
            && CreateHardLink(destination, source, IntPtr.Zero))
        {
            return;
        }
        File.Copy(source, destination, true);
    }

    private void Prune(int keep)
    {
        foreach (var path in ListRecoveryPoints().Skip(keep))
        {
            DeleteFileIfExists(path);
            DeleteFileIfExists(MatchingSettingsPath(path));
            DeleteFileIfExists(MatchingManifestPath(path));
        }
    }

    private static void DeleteFileIfExists(string path)
    {
        if (!File.Exists(path)) return;
        IOException? lastError = null;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                File.Delete(path);
                return;
            }
            catch (IOException exception) when (attempt < 3)
            {
                lastError = exception;
                SqliteConnection.ClearAllPools();
                Thread.Sleep(TimeSpan.FromMilliseconds(25 * (attempt + 1)));
            }
        }
        throw lastError ?? new IOException($"无法删除文件：{path}");
    }

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);

    private static async Task ValidateAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
        await connection.OpenAsync(cancellationToken);

        var check = connection.CreateCommand();
        check.CommandText = "PRAGMA quick_check;";
        if (!string.Equals(
                Convert.ToString(await check.ExecuteScalarAsync(cancellationToken)),
                "ok",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("恢复点数据库完整性检查失败。");
        }

        var schema = connection.CreateCommand();
        schema.CommandText = """
            SELECT CASE
              WHEN EXISTS(SELECT 1 FROM sqlite_master WHERE name='conversations')
               AND EXISTS(SELECT 1 FROM sqlite_master WHERE name='messages')
              THEN 1 ELSE 0 END;
            """;
        if (Convert.ToInt32(
                await schema.ExecuteScalarAsync(cancellationToken)) != 1)
        {
            throw new InvalidDataException("恢复点不是兼容的 AI Memory 数据库。");
        }

        var version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        if (Convert.ToInt32(
                await version.ExecuteScalarAsync(cancellationToken))
            > AIMemoryDatabase.SchemaVersion)
        {
            throw new InvalidDataException("恢复点版本高于当前应用支持的版本。");
        }
    }
}
