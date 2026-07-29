using AIMemory.Core.Persistence;
using Microsoft.Data.Sqlite;

namespace AIMemory.Core.Services;

public sealed record ChatMemImportResult(
    string SourcePath,
    string DestinationPath,
    string? BackupPath,
    int SchemaVersion);

/// <summary>
/// Imports a ChatMem-compatible SQLite store without writing to the source.
/// Both the source and the existing AI Memory database are copied through
/// SQLite's online-backup API so committed WAL data is included.
/// </summary>
public sealed class ChatMemImportService(AIMemoryDatabase destination)
{
    private static readonly SemaphoreSlim ImportGate = new(1, 1);

    public string? FindSource() =>
        DataPaths.ChatMemDatabaseCandidates.FirstOrDefault(File.Exists);

    public async Task<ChatMemImportResult> ImportAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var source = Path.GetFullPath(sourcePath);
        var target = Path.GetFullPath(destination.Path);
        if (PathsEqual(source, target))
        {
            throw new InvalidOperationException(
                "导入源不能是 AI Memory 当前数据库。");
        }
        if (!File.Exists(source))
        {
            throw new FileNotFoundException(
                "找不到 ChatMem 数据库。",
                source);
        }

        await ImportGate.WaitAsync(cancellationToken);
        try
        {
            await ValidateAsync(
                source,
                "quick_check",
                requireConversationSchema: true,
                cancellationToken);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            string? backup = null;
            if (File.Exists(target))
            {
                backup = UniqueSiblingPath(
                    target,
                    $".pre-import-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}");
                await OnlineBackupAsync(
                    target,
                    backup,
                    cancellationToken);
                await ValidateAsync(
                    backup,
                    "quick_check",
                    requireConversationSchema: true,
                    cancellationToken);
            }

            var staging = UniqueSiblingPath(
                target,
                $".importing-{Guid.NewGuid():N}");
            try
            {
                await OnlineBackupAsync(
                    source,
                    staging,
                    cancellationToken);

                var migrated = new AIMemoryDatabase(staging);
                await migrated.InitializeAsync(cancellationToken);
                await ValidateAsync(
                    staging,
                    "integrity_check",
                    requireConversationSchema: true,
                    cancellationToken);
                var version = await ReadSchemaVersionAsync(
                    staging,
                    cancellationToken);
                if (version != AIMemoryDatabase.SchemaVersion)
                {
                    throw new InvalidDataException(
                        $"导入后的数据库版本无效：{version}。");
                }

                // Microsoft.Data.Sqlite pools native handles. Release idle
                // handles before atomically replacing the destination file.
                SqliteConnection.ClearAllPools();
                if (File.Exists(target))
                {
                    File.Move(staging, target, true);
                }
                else
                {
                    File.Move(staging, target);
                }
                DeleteSidecars(target);
                await ValidateAsync(
                    target,
                    "quick_check",
                    requireConversationSchema: true,
                    cancellationToken);
                await destination.InitializeAsync(cancellationToken);

                return new ChatMemImportResult(
                    source,
                    target,
                    backup,
                    version);
            }
            catch (Exception exception)
            {
                DeleteDatabaseFiles(staging);
                throw new InvalidDataException(
                    backup is null
                        ? $"ChatMem 数据导入失败：{exception.Message}"
                        : $"ChatMem 数据导入失败；原 AI Memory 数据已保存在 {backup}：{exception.Message}",
                    exception);
            }
        }
        finally
        {
            ImportGate.Release();
        }
    }

    private static async Task OnlineBackupAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        DeleteDatabaseFiles(destinationPath);
        await using var source = new SqliteConnection(
            BuildConnectionString(sourcePath, SqliteOpenMode.ReadOnly));
        await using var target = new SqliteConnection(
            BuildConnectionString(
                destinationPath,
                SqliteOpenMode.ReadWriteCreate));
        await source.OpenAsync(cancellationToken);
        await target.OpenAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        source.BackupDatabase(target);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static async Task ValidateAsync(
        string path,
        string check,
        bool requireConversationSchema,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(
            BuildConnectionString(path, SqliteOpenMode.ReadOnly));
        await connection.OpenAsync(cancellationToken);

        var integrity = connection.CreateCommand();
        integrity.CommandText = $"PRAGMA {check};";
        var value = Convert.ToString(
            await integrity.ExecuteScalarAsync(cancellationToken));
        if (!string.Equals(value, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"数据库完整性检查失败：{path}");
        }
        if (!requireConversationSchema) return;

        var schema = connection.CreateCommand();
        schema.CommandText = """
            SELECT COUNT(*) FROM sqlite_master
            WHERE type='table' AND name IN ('conversations','messages');
            """;
        if (Convert.ToInt32(
                await schema.ExecuteScalarAsync(cancellationToken)) != 2)
        {
            throw new InvalidDataException(
                "来源不是兼容的 ChatMem/AI Memory 数据库。");
        }
    }

    private static async Task<int> ReadSchemaVersionAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(
            BuildConnectionString(path, SqliteOpenMode.ReadOnly));
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken));
    }

    private static string BuildConnectionString(
        string path,
        SqliteOpenMode mode) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString();

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            left.TrimEnd(Path.DirectorySeparatorChar),
            right.TrimEnd(Path.DirectorySeparatorChar),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static string UniqueSiblingPath(
        string path,
        string suffix)
    {
        var candidate = path + suffix;
        return !File.Exists(candidate)
            ? candidate
            : candidate + $"-{Guid.NewGuid():N}";
    }

    private static void DeleteSidecars(string path)
    {
        TryDelete(path + "-wal");
        TryDelete(path + "-shm");
    }

    private static void DeleteDatabaseFiles(string path)
    {
        TryDelete(path);
        DeleteSidecars(path);
    }

    private static void TryDelete(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
