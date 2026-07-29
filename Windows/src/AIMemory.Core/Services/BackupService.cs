using AIMemory.Core.Persistence;
using Microsoft.Data.Sqlite;

namespace AIMemory.Core.Services;

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
    {
        Directory.CreateDirectory(_backupDirectory);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
        var candidate = Path.Combine(_backupDirectory, $"aimemory-{stamp}.db");
        if (File.Exists(candidate))
        {
            stamp += $"-{Guid.NewGuid():N}";
        }
        var destination = Path.Combine(
            _backupDirectory,
            $"aimemory-{stamp}.db");

        await using var source = database.OpenConnection();
        await using var target = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = destination,
            }.ToString());
        await target.OpenAsync(cancellationToken);
        source.BackupDatabase(target);

        var settingsCopy = Path.Combine(
            _backupDirectory,
            $"settings-{stamp}.json");
        if (File.Exists(_settingsPath))
        {
            File.Copy(_settingsPath, settingsCopy, true);
        }
        return destination;
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
        File.Delete(database.Path + "-wal");
        File.Delete(database.Path + "-shm");
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

    private static async Task ValidateAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
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
