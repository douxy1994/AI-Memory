using AIMemory.Core.Persistence;
using AIMemory.Core.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AIMemory.Core.Tests;

public sealed class ChatMemImportServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "AIMemoryChatMemImportTests",
        Guid.NewGuid().ToString("N"));

    public ChatMemImportServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task ImportIncludesCommittedWalDataAndBacksUpDestination()
    {
        var sourcePath = Path.Combine(_root, "chatmem.db");
        var destinationPath = Path.Combine(_root, "aimemory.db");
        var sourceDatabase = new AIMemoryDatabase(sourcePath);
        var destinationDatabase = new AIMemoryDatabase(destinationPath);
        await sourceDatabase.InitializeAsync();
        await destinationDatabase.InitializeAsync();
        await InsertConversationAsync(
            sourceDatabase,
            "source-main",
            "Source main");
        await InsertConversationAsync(
            destinationDatabase,
            "destination-only",
            "Destination only");

        await using var writer = sourceDatabase.OpenConnection();
        await ExecuteAsync(
            writer,
            "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0;");
        await InsertConversationAsync(
            writer,
            "source-wal",
            "Source committed in WAL");
        Assert.True(File.Exists(sourcePath + "-wal"));

        var result = await new ChatMemImportService(destinationDatabase)
            .ImportAsync(sourcePath);

        Assert.Equal(AIMemoryDatabase.SchemaVersion, result.SchemaVersion);
        Assert.Equal(Path.GetFullPath(sourcePath), result.SourcePath);
        Assert.Equal(Path.GetFullPath(destinationPath), result.DestinationPath);
        Assert.NotNull(result.BackupPath);
        Assert.True(File.Exists(result.BackupPath));
        Assert.Equal(
            ["source-main", "source-wal"],
            await ConversationIdsAsync(destinationPath));
        Assert.Equal(
            ["destination-only"],
            await ConversationIdsAsync(result.BackupPath!));
        Assert.Equal(
            ["source-main", "source-wal"],
            await ConversationIdsAsync(sourcePath));
    }

    [Fact]
    public async Task InvalidSourceLeavesDestinationUnchanged()
    {
        var sourcePath = Path.Combine(_root, "invalid.db");
        var destinationPath = Path.Combine(_root, "aimemory.db");
        await File.WriteAllTextAsync(sourcePath, "not a sqlite database");
        var destination = new AIMemoryDatabase(destinationPath);
        await destination.InitializeAsync();
        await InsertConversationAsync(
            destination,
            "destination",
            "Must survive");

        await Assert.ThrowsAnyAsync<Exception>(
            () => new ChatMemImportService(destination)
                .ImportAsync(sourcePath));

        Assert.Equal(
            ["destination"],
            await ConversationIdsAsync(destinationPath));
        Assert.Empty(
            Directory.EnumerateFiles(
                _root,
                "aimemory.db.pre-import-*"));
    }

    [Fact]
    public async Task UnsupportedSourceVersionDoesNotReplaceDestination()
    {
        var sourcePath = Path.Combine(_root, "future-chatmem.db");
        var destinationPath = Path.Combine(_root, "aimemory.db");
        var source = new AIMemoryDatabase(sourcePath);
        var destination = new AIMemoryDatabase(destinationPath);
        await source.InitializeAsync();
        await destination.InitializeAsync();
        await InsertConversationAsync(source, "future", "Future source");
        await InsertConversationAsync(
            destination,
            "destination",
            "Must survive");
        await using (var connection = source.OpenConnection())
        {
            await ExecuteAsync(connection, "PRAGMA user_version=999;");
        }

        await Assert.ThrowsAsync<InvalidDataException>(
            () => new ChatMemImportService(destination)
                .ImportAsync(sourcePath));

        Assert.Equal(
            ["destination"],
            await ConversationIdsAsync(destinationPath));
        var backup = Assert.Single(
            Directory.EnumerateFiles(
                _root,
                "aimemory.db.pre-import-*"));
        Assert.Equal(
            ["destination"],
            await ConversationIdsAsync(backup));
    }

    [Fact]
    public async Task ImportRejectsCurrentDatabaseAsItsOwnSource()
    {
        var path = Path.Combine(_root, "aimemory.db");
        var database = new AIMemoryDatabase(path);
        await database.InitializeAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new ChatMemImportService(database).ImportAsync(path));

        Assert.Contains("导入源不能", exception.Message);
    }

    private static async Task InsertConversationAsync(
        AIMemoryDatabase database,
        string id,
        string summary)
    {
        await using var connection = database.OpenConnection();
        await InsertConversationAsync(connection, id, summary);
    }

    private static async Task InsertConversationAsync(
        SqliteConnection connection,
        string id,
        string summary)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO conversations(
              conversation_id,repo_id,source_agent,source_conversation_id,
              summary,started_at,updated_at,storage_path)
            VALUES($id,'repo','codex',$id,$summary,$now,$now,NULL);
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$summary", summary);
        command.Parameters.AddWithValue(
            "$now",
            DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string[]> ConversationIdsAsync(string path)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT conversation_id
            FROM conversations
            ORDER BY conversation_id;
            """;
        var ids = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetString(0));
        }
        return [.. ids];
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
