using AIMemory.Core.Persistence;

namespace AIMemory.Core.Services;

public sealed class BackupService(
    AIMemoryDatabase database,
    string? backupDirectory = null)
{
    private readonly string _backupDirectory =
        backupDirectory ?? DataPaths.BackupDirectory;

    public async Task<string> CreateRecoveryPointAsync(
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_backupDirectory);
        var destination = Path.Combine(
            _backupDirectory,
            $"aimemory-{DateTime.UtcNow:yyyyMMdd-HHmmss}.db");

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
            $"settings-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
        if (File.Exists(DataPaths.SettingsPath))
        {
            File.Copy(DataPaths.SettingsPath, settingsCopy, true);
        }
        return destination;
    }

    public IReadOnlyList<string> ListRecoveryPoints() =>
        Directory.Exists(_backupDirectory)
            ? Directory.EnumerateFiles(_backupDirectory, "aimemory-*.db")
                .OrderDescending()
                .ToArray()
            : [];
}
