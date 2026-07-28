using System.Reflection;
using Microsoft.Data.Sqlite;

namespace AIMemory.Core.Persistence;

public sealed class AIMemoryDatabase(string? path = null)
{
    public const int SchemaVersion = 1;
    public string Path { get; } = path ?? DataPaths.DatabasePath;

    private string ConnectionString =>
        new SqliteConnectionStringBuilder
        {
            DataSource = Path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var existed = File.Exists(Path);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);

        var version = await ScalarIntAsync(
            connection, "PRAGMA user_version;", cancellationToken);
        if (version > SchemaVersion)
        {
            throw new InvalidOperationException(
                $"数据库版本 {version} 高于当前 Windows 应用支持的版本 {SchemaVersion}。");
        }
        if (existed && version < SchemaVersion)
        {
            await CreateBackupAsync(connection, version, cancellationToken);
        }
        if (version < SchemaVersion)
        {
            var schema = await ReadSchemaAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(
                cancellationToken);
            var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = schema;
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
    }

    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private static async Task<int> ScalarIntAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result);
    }

    private async Task CreateBackupAsync(
        SqliteConnection source,
        int version,
        CancellationToken cancellationToken)
    {
        var backupPath = $"{Path}.backup-v{version}-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
        await using var destination = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = backupPath }.ToString());
        await destination.OpenAsync(cancellationToken);
        source.BackupDatabase(destination);
    }

    private static async Task<string> ReadSchemaAsync(
        CancellationToken cancellationToken)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames()
            .Single(value => value.EndsWith("SchemaV1.sql", StringComparison.Ordinal));
        await using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("找不到内置数据库架构。");
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken);
    }
}
