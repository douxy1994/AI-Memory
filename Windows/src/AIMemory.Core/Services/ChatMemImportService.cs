using Microsoft.Data.Sqlite;
using AIMemory.Core.Persistence;

namespace AIMemory.Core.Services;

public sealed class ChatMemImportService(AIMemoryDatabase destination)
{
    public string? FindSource() =>
        DataPaths.ChatMemDatabaseCandidates.FirstOrDefault(File.Exists);

    public async Task<string> ImportAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("找不到 ChatMem 数据库。", sourcePath);
        }

        await ValidateAsync(sourcePath, cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(destination.Path)!);
        var backup = destination.Path + $".pre-import-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
        if (File.Exists(destination.Path))
        {
            File.Copy(destination.Path, backup, true);
        }

        var temporary = destination.Path + ".importing";
        File.Copy(sourcePath, temporary, true);
        try
        {
            await ValidateAsync(temporary, cancellationToken);
            File.Move(temporary, destination.Path, true);
            await destination.InitializeAsync(cancellationToken);
            return backup;
        }
        catch
        {
            File.Delete(temporary);
            throw;
        }
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
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CASE
              WHEN EXISTS(SELECT 1 FROM sqlite_master WHERE name='conversations')
               AND EXISTS(SELECT 1 FROM sqlite_master WHERE name='messages')
              THEN 1 ELSE 0 END;
            """;
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) != 1)
        {
            throw new InvalidDataException("来源不是兼容的 ChatMem/AI Memory 数据库。");
        }
    }
}
