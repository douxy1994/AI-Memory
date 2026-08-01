using AIMemory.Core.Models;
using AIMemory.Core.Persistence;
using AIMemory.Core.Services;
using Microsoft.Data.Sqlite;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Xunit;

namespace AIMemory.Core.Tests;

public sealed class CoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "AIMemoryWindowsTests", Guid.NewGuid().ToString("N"));

    public CoreTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void CloudReadinessReportsMissingFolders()
    {
        var result = new CloudReadinessService().Check(
            Path.Combine(_root, "missing-cloud-folder"));

        Assert.False(result.FolderExists);
        Assert.True(result.IsQuiet);
        Assert.False(result.HasLockFiles);
        Assert.Equal("folder_missing", result.RecommendedAction);
    }

    [Fact]
    public void CloudReadinessDetectsLockFiles()
    {
        var folder = Path.Combine(_root, "locked-cloud-folder");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "~$document.docx"), "");
        Directory.SetLastWriteTimeUtc(
            folder,
            DateTime.UtcNow.AddMinutes(-1));

        var result = new CloudReadinessService().Check(folder);

        Assert.True(result.FolderExists);
        Assert.False(result.IsQuiet);
        Assert.True(result.HasLockFiles);
        Assert.Equal("wait", result.RecommendedAction);
    }

    [Fact]
    public void CloudReadinessRequiresAQuietPeriod()
    {
        var folder = Path.Combine(_root, "quiet-cloud-folder");
        Directory.CreateDirectory(folder);
        var now = DateTimeOffset.UtcNow;

        var busy = new CloudReadinessService().Check(folder, now);
        Assert.False(busy.IsQuiet);
        Assert.False(busy.HasLockFiles);

        Directory.SetLastWriteTimeUtc(
            folder,
            now.UtcDateTime.AddSeconds(-10));
        var ready = new CloudReadinessService().Check(folder, now);
        Assert.True(ready.IsQuiet);
        Assert.Equal("safe_to_sync", ready.RecommendedAction);
    }

    [Fact]
    public async Task UpgradeReadinessPassesWithDefaultsAndValidDatabase()
    {
        var settingsPath = Path.Combine(
            _root,
            "readiness-defaults",
            "settings.json");
        var database = new AIMemoryDatabase(
            Path.Combine(_root, "readiness-defaults", "aimemory.db"));
        await database.InitializeAsync();

        var report = await new UpgradeReadinessService(
            database,
            new SettingsStore(settingsPath),
            settingsPath).CheckAsync(_ => false);

        Assert.Equal("ok", report.Status);
        Assert.Equal(0, report.ErrorCount);
        Assert.Equal(0, report.WarningCount);
        Assert.Contains(
            report.Checks,
            value => value.Key == "settings"
                && value.DetailCode == "settings_defaults");
        Assert.Contains(
            report.Checks,
            value => value.Key == "memory_store"
                && value.DetailCode == "database_valid");
    }

    [Fact]
    public async Task UpgradeReadinessWarnsForIncompleteWebDavCredentials()
    {
        var root = Path.Combine(_root, "readiness-webdav");
        var settingsPath = Path.Combine(root, "settings.json");
        var settingsStore = new SettingsStore(settingsPath);
        await settingsStore.SaveAsync(new AppSettings
        {
            Sync = new SyncSettings
            {
                Provider = "webdav",
                WebdavHost = "dav.example.test",
                Username = "alvis",
                RemotePath = "",
            },
        });
        var database = new AIMemoryDatabase(
            Path.Combine(root, "aimemory.db"));
        await database.InitializeAsync();

        var report = await new UpgradeReadinessService(
            database,
            settingsStore,
            settingsPath).CheckAsync(_ => false);

        Assert.Equal("warning", report.Status);
        Assert.Equal(0, report.ErrorCount);
        Assert.Equal(2, report.WarningCount);
        Assert.Contains(
            report.Checks,
            value => value.DetailCode == "webdav_incomplete");
        Assert.Contains(
            report.Checks,
            value => value.DetailCode == "password_missing");
    }

    [Fact]
    public async Task UpgradeReadinessReportsInvalidSettingsAsBlocking()
    {
        var root = Path.Combine(_root, "readiness-invalid-settings");
        Directory.CreateDirectory(root);
        var settingsPath = Path.Combine(root, "settings.json");
        await File.WriteAllTextAsync(settingsPath, "{invalid-json");
        var database = new AIMemoryDatabase(
            Path.Combine(root, "aimemory.db"));
        await database.InitializeAsync();

        var report = await new UpgradeReadinessService(
            database,
            new SettingsStore(settingsPath),
            settingsPath).CheckAsync(_ => true);

        Assert.Equal("error", report.Status);
        Assert.Equal(1, report.ErrorCount);
        Assert.Contains(
            report.Checks,
            value => value.DetailCode == "settings_invalid");
    }

    [Fact]
    public async Task UpgradeReadinessReportsInvalidDatabaseAsBlocking()
    {
        var root = Path.Combine(_root, "readiness-invalid-database");
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "aimemory.db");
        await File.WriteAllTextAsync(databasePath, "not-a-database");
        var settingsPath = Path.Combine(root, "settings.json");

        var report = await new UpgradeReadinessService(
            new AIMemoryDatabase(databasePath),
            new SettingsStore(settingsPath),
            settingsPath).CheckAsync(_ => false);

        Assert.Equal("error", report.Status);
        Assert.Equal(1, report.ErrorCount);
        Assert.Contains(
            report.Checks,
            value => value.DetailCode == "database_invalid");
    }

    [Theory]
    [InlineData(null, "system", "")]
    [InlineData("", "system", "")]
    [InlineData("system", "system", "")]
    [InlineData("zh-CN", "zh-Hans", "zh-CN")]
    [InlineData("zh-Hans", "zh-Hans", "zh-CN")]
    [InlineData("en-US", "en", "en-US")]
    [InlineData("en", "en", "en-US")]
    [InlineData("unsupported", "system", "")]
    public void LanguagePreferenceNormalizesCompatibleValues(
        string? value,
        string expectedId,
        string expectedTag)
    {
        Assert.Equal(
            expectedId,
            LanguagePreferenceService.NormalizeId(value));
        Assert.Equal(
            expectedTag,
            LanguagePreferenceService.ResolveWindowsLanguageTag(value));
    }

    [Fact]
    public async Task DatabaseCreatesMacCompatibleVersionOneSchema()
    {
        var database = new AIMemoryDatabase(Path.Combine(_root, "aimemory.db"));
        await database.InitializeAsync();
        await using var connection = database.OpenConnection();

        var version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        Assert.Equal(1L, (long)(await version.ExecuteScalarAsync() ?? -1L));

        var tables = connection.CreateCommand();
        tables.CommandText = """
            SELECT name FROM sqlite_master
            WHERE type='table' AND name IN (
              'conversations','messages','approved_memories',
              'checkpoints','handoff_packets','agent_runs','artifacts')
            ORDER BY name;
            """;
        var names = new List<string>();
        await using var reader = await tables.ExecuteReaderAsync();
        while (await reader.ReadAsync()) names.Add(reader.GetString(0));
        Assert.Equal(7, names.Count);

        var indexes = connection.CreateCommand();
        indexes.CommandText = """
            SELECT name FROM sqlite_master
            WHERE type='index' AND name IN (
              'idx_conversations_updated_at',
              'idx_messages_conversation_id',
              'idx_file_changes_conversation_id')
            ORDER BY name;
            """;
        var indexNames = new List<string>();
        await using var indexReader = await indexes.ExecuteReaderAsync();
        while (await indexReader.ReadAsync()) indexNames.Add(indexReader.GetString(0));
        Assert.Equal(
            [
                "idx_conversations_updated_at",
                "idx_file_changes_conversation_id",
                "idx_messages_conversation_id",
            ],
            indexNames);
    }

    [Fact]
    public async Task RecoveryPointRestoreValidatesBacksUpAndReplacesDatabase()
    {
        var databasePath = Path.Combine(_root, "restore.db");
        var backupDirectory = Path.Combine(_root, "backups");
        var settingsPath = Path.Combine(_root, "settings.json");
        var database = new AIMemoryDatabase(databasePath);
        await database.InitializeAsync();
        await File.WriteAllTextAsync(settingsPath, """{"marker":"old"}""");
        await InsertRestoreConversationAsync(
            database, "before", "Before restore");

        var service = new BackupService(
            database, backupDirectory, settingsPath);
        var recoveryPoint = await service.CreateRecoveryPointAsync();
        await File.WriteAllTextAsync(settingsPath, """{"marker":"new"}""");
        await InsertRestoreConversationAsync(
            database, "after", "After restore");

        var safetyBackup = await service.RestoreRecoveryPointAsync(recoveryPoint);
        var conversations = await new ConversationRepository(database).ListAsync();
        Assert.Single(conversations);
        Assert.Equal("before", conversations[0].Id);
        Assert.True(File.Exists(safetyBackup));
        Assert.NotEqual(recoveryPoint, safetyBackup);
        var safetyDatabase = new AIMemoryDatabase(safetyBackup);
        Assert.Equal(
            2,
            (await new ConversationRepository(safetyDatabase).ListAsync()).Count);
        Assert.Contains(
            "\"marker\":\"old\"",
            await File.ReadAllTextAsync(settingsPath));

        var invalid = Path.Combine(backupDirectory, "aimemory-invalid.db");
        await File.WriteAllTextAsync(invalid, "not sqlite");
        await Assert.ThrowsAnyAsync<Exception>(
            () => service.RestoreRecoveryPointAsync(invalid));
        Assert.Single(await new ConversationRepository(database).ListAsync());
    }

    [Fact]
    public async Task IncrementalBackupSkipsUnchangedAndTracksChangedComponents()
    {
        var databasePath = Path.Combine(_root, "incremental.db");
        var backupDirectory = Path.Combine(_root, "incremental-backups");
        var settingsPath = Path.Combine(_root, "incremental-settings.json");
        var database = new AIMemoryDatabase(databasePath);
        await database.InitializeAsync();
        await InsertRestoreConversationAsync(
            database, "incremental", "Incremental backup");
        await File.WriteAllTextAsync(settingsPath, """{"marker":"one"}""");
        var service = new BackupService(
            database, backupDirectory, settingsPath);

        var first = await service.CreateRecoveryPointDetailedAsync("manual");
        var unchanged = await service.CreateRecoveryPointDetailedAsync("manual");

        Assert.True(first.Created);
        Assert.True(first.DatabaseChanged);
        Assert.True(first.SettingsChanged);
        Assert.False(unchanged.Created);
        Assert.Equal(first.Path, unchanged.Path);
        Assert.Single(service.ListRecoveryPoints());

        await File.WriteAllTextAsync(settingsPath, """{"marker":"two"}""");
        var settingsOnly =
            await service.CreateRecoveryPointDetailedAsync("settings");
        Assert.True(settingsOnly.Created);
        Assert.False(settingsOnly.DatabaseChanged);
        Assert.True(settingsOnly.SettingsChanged);

        await InsertRestoreConversationAsync(
            database, "incremental-two", "Database changed");
        var databaseChanged =
            await service.CreateRecoveryPointDetailedAsync("database");
        Assert.True(databaseChanged.Created);
        Assert.True(databaseChanged.DatabaseChanged);
        Assert.False(databaseChanged.SettingsChanged);
        Assert.Equal(3, service.ListRecoveryPoints().Count);
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("gemini")]
    [InlineData("opencode")]
    public async Task NativeConversationCopyWritesAndReimportsTargetStore(
        string target)
    {
        var home = Path.Combine(_root, $"migration-{target}");
        Directory.CreateDirectory(home);
        if (target == "opencode")
        {
            await CreateOpenCodeMigrationStoreAsync(home);
        }
        var database = new AIMemoryDatabase(
            Path.Combine(home, "aimemory.db"));
        await database.InitializeAsync();
        var repository = new ConversationRepository(database);
        var sourceId = $"source-{target}";
        await repository.UpsertAsync(new WebDavConversationDetail(
            sourceId,
            "hermes",
            @"C:\repo",
            "2026-07-01T01:00:00Z",
            "2026-07-01T01:01:00Z",
            "Migration fixture",
            null,
            null,
            [
                new WebDavMessage(
                    $"user-{target}",
                    "2026-07-01T01:00:00Z",
                    "user",
                    "Preserve this question",
                    [],
                    []),
                new WebDavMessage(
                    $"assistant-{target}",
                    "2026-07-01T01:01:00Z",
                    "assistant",
                    "Preserve this answer",
                    [
                        new WebDavToolCall(
                            $"tool-{target}",
                            "read_file",
                            JsonSerializer.SerializeToElement(
                                new { path = "README.md" }),
                            "file contents",
                            "success"),
                    ],
                    []),
            ],
            [
                new WebDavFileChange(
                    "README.md",
                    "modified",
                    "2026-07-01T01:01:00Z",
                    $"assistant-{target}"),
            ]));

        var result = await new ConversationMigrationService(repository, home)
            .CopyAsync("hermes", target, sourceId);

        Assert.True(result.Verified);
        Assert.Equal(2, result.SourceMessageCount);
        Assert.Equal(2, result.TargetMessageCount);
        Assert.Equal(1, result.SourceToolCallCount);
        Assert.Equal(1, result.TargetToolCallCount);
        Assert.Equal(1, result.SourceFileCount);
        Assert.Equal(1, result.TargetFileCount);
        Assert.True(result.FirstUserPreserved);
        Assert.NotNull(await repository.FindAsync(sourceId));
        var migrated = await repository.ExportAsync(result.NewId);
        Assert.Equal(target, migrated.SourceAgent);
        Assert.Equal(
            ["Preserve this question", "Preserve this answer"],
            migrated.Messages.Select(value => value.Content).ToArray());
        var migratedTool = Assert.Single(migrated.Messages[1].ToolCalls);
        Assert.Equal("read_file", migratedTool.Name);
        Assert.Equal("file contents", migratedTool.Output);
        Assert.Equal("success", migratedTool.Status);
        Assert.Equal(
            "README.md",
            Assert.Single(migrated.FileChanges).Path);
    }

    [Fact]
    public async Task CutMigrationArchivesAndTrashRestoreRecoversRawSource()
    {
        var home = Path.Combine(_root, "cut-migration");
        var sourceDirectory = Path.Combine(
            home, ".claude", "projects", "C--repo");
        Directory.CreateDirectory(sourceDirectory);
        var sourcePath = Path.Combine(sourceDirectory, "source-cut.jsonl");
        await File.WriteAllTextAsync(
            sourcePath,
            """{"type":"user","timestamp":"2026-07-01T01:00:00Z","cwd":"C:\\repo","message":{"role":"user","content":"Original raw source"}}""");
        var database = new AIMemoryDatabase(
            Path.Combine(home, "aimemory.db"));
        await database.InitializeAsync();
        var repository = new ConversationRepository(database);
        var input = JsonSerializer.SerializeToElement(
            new { path = "README.md" });
        await repository.UpsertAsync(new WebDavConversationDetail(
            "source-cut",
            "claude",
            @"C:\repo",
            "2026-07-01T01:00:00Z",
            "2026-07-01T01:01:00Z",
            "Cut fixture",
            sourcePath,
            "claude --resume source-cut",
            [
                new WebDavMessage(
                    "cut-user",
                    "2026-07-01T01:00:00Z",
                    "user",
                    "Cut question",
                    [],
                    []),
                new WebDavMessage(
                    "cut-assistant",
                    "2026-07-01T01:01:00Z",
                    "assistant",
                    "Cut answer",
                    [
                        new WebDavToolCall(
                            "cut-tool",
                            "read_file",
                            input,
                            "contents",
                            "success"),
                    ],
                    []),
            ],
            [
                new WebDavFileChange(
                    "README.md",
                    "modified",
                    "2026-07-01T01:01:00Z",
                    "cut-assistant"),
            ]));
        var archiveRoot = Path.Combine(home, "trash", "raw");
        var writer = new NativeAgentConversationWriter(home, archiveRoot);
        var trash = new TrashService(
            database,
            Path.Combine(home, "trash"),
            null,
            writer);
        var migration = new ConversationMigrationService(
            repository, home, writer);

        var result = await migration.MigrateAsync(
            "claude",
            "gemini",
            "source-cut",
            "cut",
            trash);

        Assert.True(result.CutDeletedSource);
        Assert.False(File.Exists(sourcePath));
        Assert.Null(await repository.FindAsync("source-cut"));
        var record = Assert.Single(await trash.ListAsync());
        await trash.RestoreAsync(record);
        Assert.True(File.Exists(sourcePath));
        var restored = await repository.ExportAsync("source-cut");
        Assert.Single(restored.Messages[1].ToolCalls);
        Assert.Single(restored.FileChanges);

        var summary = await repository.FindAsync("source-cut");
        Assert.NotNull(summary);
        var archive = await writer.ArchiveSourceAsync(restored);
        var deleteRecord = await trash.TrashAsync(summary!, 14, archive);
        Assert.True(File.Exists(archive.BackupPath));
        await trash.DeleteAsync(deleteRecord);
        Assert.False(File.Exists(archive.BackupPath));
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("opencode")]
    public async Task DatabaseBackedCutMigrationRestoresAndPurgesRawSource(
        string source)
    {
        var home = Path.Combine(_root, $"cut-{source}");
        Directory.CreateDirectory(home);
        if (source == "opencode")
        {
            await CreateOpenCodeMigrationStoreAsync(home);
        }
        var database = new AIMemoryDatabase(
            Path.Combine(home, "aimemory.db"));
        await database.InitializeAsync();
        var repository = new ConversationRepository(database);
        var writer = new NativeAgentConversationWriter(
            home, Path.Combine(home, "trash", "raw"));
        var seed = new WebDavConversationDetail(
            $"seed-{source}",
            "hermes",
            @"C:\repo",
            "2026-07-02T01:00:00Z",
            "2026-07-02T01:01:00Z",
            "Database-backed cut fixture",
            null,
            null,
            [
                new WebDavMessage(
                    $"seed-user-{source}",
                    "2026-07-02T01:00:00Z",
                    "user",
                    "Database-backed question",
                    [],
                    []),
                new WebDavMessage(
                    $"seed-assistant-{source}",
                    "2026-07-02T01:01:00Z",
                    "assistant",
                    "Database-backed answer",
                    [],
                    []),
            ],
            []);
        var written = await writer.WriteAsync(seed, source);
        await new NativeHistoryImportService(repository, home)
            .ImportAllAsync();
        var indexed = await repository.ExportAsync(written.Id);
        Assert.Equal(source, indexed.SourceAgent);
        await AssertDatabaseBackedSourceStateAsync(
            home, source, written, "active");

        var trash = new TrashService(
            database,
            Path.Combine(home, "trash"),
            null,
            writer);
        var migration = new ConversationMigrationService(
            repository, home, writer);
        var result = await migration.MigrateAsync(
            source,
            "gemini",
            written.Id,
            "cut",
            trash);

        Assert.True(result.Verified);
        Assert.True(result.CutDeletedSource);
        Assert.Null(await repository.FindAsync(written.Id));
        await AssertDatabaseBackedSourceStateAsync(
            home, source, written, "archived");

        var restoreRecord = Assert.Single(await trash.ListAsync());
        await trash.RestoreAsync(restoreRecord);
        await AssertDatabaseBackedSourceStateAsync(
            home, source, written, "active");
        var restored = await repository.ExportAsync(written.Id);
        Assert.Equal(
            ["Database-backed question", "Database-backed answer"],
            restored.Messages.Select(message => message.Content).ToArray());

        await new NativeHistoryImportService(repository, home)
            .ImportAllAsync();
        Assert.NotNull(await repository.FindAsync(written.Id));
        restored = await repository.ExportAsync(written.Id);
        var summary = await repository.FindAsync(written.Id);
        Assert.NotNull(summary);
        var archive = await writer.ArchiveSourceAsync(restored);
        var deleteRecord = await trash.TrashAsync(
            summary!,
            14,
            archive,
            detailOverride: restored);
        await trash.DeleteAsync(deleteRecord);
        await AssertDatabaseBackedSourceStateAsync(
            home, source, written, "missing");
        if (!string.IsNullOrWhiteSpace(archive.BackupPath))
        {
            Assert.False(File.Exists(archive.BackupPath));
        }
    }

    [Fact]
    public async Task CutMigrationRejectsUnarchivableSourceBeforeTargetWrite()
    {
        var home = Path.Combine(_root, "cut-unarchivable");
        var database = new AIMemoryDatabase(
            Path.Combine(home, "aimemory.db"));
        await database.InitializeAsync();
        var repository = new ConversationRepository(database);
        await repository.UpsertAsync(new WebDavConversationDetail(
            "hermes-cut",
            "hermes",
            @"C:\repo",
            "2026-07-03T01:00:00Z",
            "2026-07-03T01:00:00Z",
            "Hermes source",
            null,
            null,
            [
                new WebDavMessage(
                    "hermes-user",
                    "2026-07-03T01:00:00Z",
                    "user",
                    "Keep the source",
                    [],
                    []),
            ],
            []));
        var migration = new ConversationMigrationService(repository, home);
        var exception = await Assert.ThrowsAsync<NotSupportedException>(
            () => migration.MigrateAsync(
                "hermes",
                "gemini",
                "hermes-cut",
                "cut",
                new TrashService(
                    database, Path.Combine(home, "trash"))));

        Assert.Contains("请选择复制", exception.Message);
        Assert.NotNull(await repository.FindAsync("hermes-cut"));
        Assert.False(Directory.Exists(Path.Combine(home, ".gemini")));
    }

    [Fact]
    public async Task ConversationListIncludesProjectPathForWorkbenchGrouping()
    {
        var database = new AIMemoryDatabase(Path.Combine(_root, "projects.db"));
        await database.InitializeAsync();
        await using (var connection = database.OpenConnection())
        {
            var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO repos(
                  repo_id,repo_root,repo_fingerprint,git_remote,
                  default_branch,created_at,updated_at)
                VALUES(
                  'repo','C:\src\AI-Memory','fingerprint',NULL,NULL,$now,$now);
                INSERT INTO conversations VALUES(
                  'c1','repo','codex','source','title',$now,$now,NULL);
                """;
            insert.Parameters.AddWithValue(
                "$now", DateTimeOffset.UtcNow.ToString("O"));
            await insert.ExecuteNonQueryAsync();
        }
        var conversation = Assert.Single(
            await new ConversationRepository(database).ListAsync());
        Assert.Equal(@"C:\src\AI-Memory", conversation.ProjectPath);
    }

    [Fact]
    public void MachineGroupingDetectsPlatformsAndAppliesNamesAndOverrides()
    {
        var now = DateTimeOffset.Parse("2026-07-29T04:00:00Z");
        var conversations = new[]
        {
            new ConversationSummary(
                "windows",
                "windows-repo",
                "codex",
                "source-windows",
                "Windows work",
                now.AddMinutes(-3),
                now.AddMinutes(-3),
                null,
                @"C:\src\AI-Memory"),
            new ConversationSummary(
                "mac",
                "mac-repo",
                "claude",
                "source-mac",
                "Mac work",
                now.AddMinutes(-2),
                now.AddMinutes(-2),
                null,
                "/Users/alvis/AI-Memory"),
            new ConversationSummary(
                "linux",
                "linux-repo",
                "gemini",
                "source-linux",
                "Linux work",
                now.AddMinutes(-1),
                now.AddMinutes(-1),
                null,
                "/home/alvis/AI-Memory"),
        };
        var settings = new AppSettings
        {
            MachineGroupNames =
            {
                ["macos"] = "MacBook Pro",
            },
            MachineGroupOverrides =
            {
                ["/home/alvis/AI-Memory"] = "macos",
            },
        };

        var service = new MachineGroupingService();
        var groups = service.Build(conversations, settings);

        Assert.Equal("windows", MachineGroupingService.DetectMachineId(
            @"C:\src\AI-Memory"));
        Assert.Equal("macos", MachineGroupingService.DetectMachineId(
            "/Volumes/Work/AI-Memory"));
        Assert.Equal("linux", MachineGroupingService.DetectMachineId(
            "/opt/ai-memory"));
        Assert.Equal("internal", MachineGroupingService.DetectMachineId(
            "chatmem://local"));
        Assert.Equal("other", MachineGroupingService.DetectMachineId(
            "relative/project"));
        Assert.Equal(2, groups.Count);
        Assert.Equal("MacBook Pro", groups[0].Label);
        Assert.Equal(2, groups[0].ConversationCount);
        Assert.Contains(
            groups[0].Projects,
            project => project.Path == "/home/alvis/AI-Memory"
                && project.MachineId == "macos"
                && project.MachineLabel == "MacBook Pro");
        Assert.Equal("Windows", groups[1].Label);
    }

    [Fact]
    public async Task SettingsNormalizeAndPersistWithoutDroppingExtensions()
    {
        var path = Path.Combine(_root, "settings.json");
        var store = new SettingsStore(path);
        var settings = new AppSettings
        {
            TrashRetentionDays = 999,
            AutoCaptureMemory = false,
            Sync = new SyncSettings { WebdavHost = "dav.example.com" },
        };
        await store.SaveAsync(settings);
        var loaded = await store.LoadAsync();
        Assert.Equal(365, loaded.TrashRetentionDays);
        Assert.False(loaded.AutoCaptureMemory);
        Assert.Equal("dav.example.com", loaded.Sync.WebdavHost);
    }

    [Fact]
    public async Task WindowsSettingsImportMacCanonicalAndLegacyKeys()
    {
        var path = Path.Combine(_root, "mac-settings.json");
        await File.WriteAllTextAsync(path, """
            {
              "schemaVersion": 3,
              "locale": "en",
              "font_family": "source-serif",
              "auto_capture_memory": false,
              "trash_retention_days": 21,
              "unrelatedExtension": "preserved",
              "sync": {
                "webdav_scheme": "http",
                "webdav_host": "dav.example.test",
                "webdav_username": "alvis",
                "sync_folder": "D:\\AI Memory"
              }
            }
            """);
        var store = new SettingsStore(path);

        var settings = await store.LoadAsync();

        Assert.Equal(3, settings.SettingsVersion);
        Assert.Equal("en", settings.Language);
        Assert.Equal("source-serif", settings.FontFamily);
        Assert.False(settings.AutoCaptureMemory);
        Assert.Equal(21, settings.TrashRetentionDays);
        Assert.Equal("http", settings.Sync.WebdavScheme);
        Assert.Equal("dav.example.test", settings.Sync.WebdavHost);
        Assert.Equal("alvis", settings.Sync.Username);
        Assert.Equal(@"D:\AI Memory", settings.Sync.SyncFolder);
        Assert.NotNull(settings.ExtensionData);
        Assert.True(settings.ExtensionData!.ContainsKey(
            "unrelatedExtension"));

        await store.SaveAsync(settings);
        var saved = await File.ReadAllTextAsync(path);
        Assert.Contains("\"settingsVersion\": 3", saved);
        Assert.Contains("\"language\": \"en\"", saved);
        Assert.Contains("\"unrelatedExtension\": \"preserved\"", saved);
        Assert.DoesNotContain("\"locale\"", saved);
        Assert.DoesNotContain("font_family", saved);
        Assert.DoesNotContain("webdav_host", saved);
    }

    [Fact]
    public async Task AutomaticCaptureRefreshesAndUpsertsOneRecoveryPoint()
    {
        var home = Path.Combine(_root, "automatic-capture");
        var sourceDirectory = Path.Combine(
            home,
            ".claude",
            "projects",
            "C--repo");
        Directory.CreateDirectory(sourceDirectory);
        var sourcePath = Path.Combine(sourceDirectory, "auto-1.jsonl");
        await File.WriteAllLinesAsync(sourcePath,
        [
            """{"type":"user","uuid":"u1","timestamp":"2026-07-01T01:00:00Z","cwd":"C:\\repo","message":{"role":"user","content":"First question"}}""",
            """{"type":"assistant","uuid":"a1","timestamp":"2026-07-01T01:01:00Z","cwd":"C:\\repo","message":{"role":"assistant","content":"First answer"}}""",
        ]);
        var database = new AIMemoryDatabase(
            Path.Combine(home, "aimemory.db"));
        await database.InitializeAsync();
        var repository = new ConversationRepository(database);
        var service = new AutomaticCaptureService(
            database,
            repository,
            home);

        var first = await service.CaptureAsync("claude", "auto-1");
        Assert.Equal(2, first.Detail.Messages.Count);
        Assert.Equal("claude:auto-1", first.Checkpoint.ConversationId);
        Assert.Contains(
            "\"capture\":\"auto\"",
            first.Checkpoint.MetadataJson);

        await File.AppendAllTextAsync(
            sourcePath,
            Environment.NewLine
            + """{"type":"assistant","uuid":"a2","timestamp":"2026-07-01T01:02:00Z","cwd":"C:\\repo","message":{"role":"assistant","content":"Updated answer"}}""");
        var second = await service.CaptureAsync("claude", "auto-1");

        Assert.Equal(first.Checkpoint.Id, second.Checkpoint.Id);
        Assert.Equal(3, second.Detail.Messages.Count);
        Assert.Contains(
            "\"message_count\":3",
            second.Checkpoint.MetadataJson);
        Assert.Single(
            await new RecoveryService(database).ListCheckpointsAsync(),
            value => value.MetadataJson.Contains(
                "\"capture\":\"auto\"",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task AutomaticCheckpointNeverReplacesManualOrHandoffPoint()
    {
        var database = new AIMemoryDatabase(
            Path.Combine(_root, "automatic-checkpoints.db"));
        await database.InitializeAsync();
        var repository = new ConversationRepository(database);
        await repository.UpsertAsync(new WebDavConversationDetail(
            "capture-1",
            "codex",
            @"C:\repo",
            "2026-07-01T01:00:00Z",
            "2026-07-01T01:01:00Z",
            "Capture fixture",
            null,
            "codex resume capture-1",
            [
                new WebDavMessage(
                    "m1",
                    "2026-07-01T01:00:00Z",
                    "user",
                    "Remember this",
                    [],
                    []),
            ],
            []));
        var conversation = await repository.FindAsync("capture-1");
        Assert.NotNull(conversation);
        var recovery = new RecoveryService(database);
        var manual = await recovery.CreateCheckpointAsync(conversation!, 1);
        var automatic = await recovery.UpsertAutomaticCheckpointAsync(
            conversation,
            "codex:capture-1",
            "Automatic one",
            null,
            """{"capture":"auto","message_count":1}""");
        var updated = await recovery.UpsertAutomaticCheckpointAsync(
            conversation,
            "codex:capture-1",
            "Automatic two",
            null,
            """{"capture":"auto","message_count":2}""");

        Assert.Equal(automatic.Id, updated.Id);
        Assert.Equal(2, (await recovery.ListCheckpointsAsync()).Count);
        Assert.Contains(
            await recovery.ListCheckpointsAsync(),
            value => value.Id == manual.Id
                && value.MetadataJson.Contains(
                    "\"capture\":\"manual\"",
                    StringComparison.Ordinal));

        await recovery.CreateHandoffAsync(updated, "claude");
        var replacement = await recovery.UpsertAutomaticCheckpointAsync(
            conversation,
            "codex:capture-1",
            "Automatic three",
            null,
            """{"capture":"auto","message_count":3}""");
        Assert.NotEqual(updated.Id, replacement.Id);
        Assert.Equal(3, (await recovery.ListCheckpointsAsync()).Count);
    }

    [Theory]
    [InlineData("system", "system", "Segoe UI Variable")]
    [InlineData("Segoe UI Variable", "system", "Segoe UI Variable")]
    [InlineData("sourceSans", "source-sans", "Noto Sans CJK SC")]
    [InlineData("source-sans", "source-sans", "Noto Sans CJK SC")]
    [InlineData("sourceSerif", "source-serif", "Noto Serif CJK SC")]
    [InlineData("Noto Serif CJK SC", "source-serif", "Noto Serif CJK SC")]
    [InlineData("wenkai", "wenkai", "LXGW WenKai")]
    [InlineData("unknown-font", "system", "Segoe UI Variable")]
    public void FontPreferencesNormalizeMacAndLegacyWindowsValues(
        string input,
        string expectedId,
        string expectedWindowsFamily)
    {
        Assert.Equal(expectedId, FontPreferenceService.NormalizeId(input));
        Assert.Equal(
            expectedWindowsFamily,
            FontPreferenceService.ResolveWindowsFamily(input));
    }

    [Fact]
    public async Task FavoritesCanBeCreatedEditedPinnedAndRemoved()
    {
        var store = new SettingsStore(Path.Combine(_root, "favorites.json"));
        var service = new FavoriteService(store);
        var conversation = new ConversationSummary(
            "c1",
            "repo",
            "codex",
            "source",
            "修复同步",
            DateTimeOffset.Parse("2026-07-27T01:00:00Z"),
            DateTimeOffset.Parse("2026-07-28T02:00:00Z"),
            null);

        Assert.True(await service.ToggleAsync(conversation, @"C:\repo"));
        Assert.True(await service.IsFavoriteAsync("codex", "c1"));
        await service.UpdateAsync(
            "codex",
            "c1",
            "继续验证 WebDAV",
            ["sync", "release", "sync"],
            pinned: true);

        var favorite = Assert.Single(
            (await store.LoadAsync()).FavoriteConversations).Value;
        Assert.True(favorite.Pinned);
        Assert.Equal("继续验证 WebDAV", favorite.Note);
        Assert.Equal(["release", "sync"], favorite.Tags);
        var card = FavoriteService.ContinuationCard(favorite);
        Assert.Contains("Use AI Memory", card);
        Assert.DoesNotContain("ChatMem", card);

        await service.RemoveAsync("codex", "c1");
        Assert.False(await service.IsFavoriteAsync("codex", "c1"));
    }

    [Fact]
    public async Task HistoryProjectionsRetainClickableConversationLinks()
    {
        var database = new AIMemoryDatabase(Path.Combine(_root, "history.db"));
        await database.InitializeAsync();
        await using (var connection = database.OpenConnection())
        {
            var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO agent_runs VALUES(
                  'run:c1','repo','codex','task','complete','summary',
                  '2026-07-28T01:00:00Z','2026-07-28T02:00:00Z');
                INSERT INTO artifacts VALUES(
                  'a1','run:c1','patch','Changed files','artifact summary',
                  NULL,NULL,'verified','2026-07-28T02:00:00Z');
                INSERT INTO episodes VALUES(
                  'e1','repo','Fixed sync','episode summary','success',
                  '2026-07-28T02:00:00Z','c1');
                INSERT INTO wiki_pages VALUES(
                  'w1','repo','sync','Sync notes','wiki body','active',
                  '[]','[]','2026-07-28T02:00:00Z',NULL,
                  '2026-07-28T01:00:00Z','2026-07-28T02:00:00Z');
                """;
            await insert.ExecuteNonQueryAsync();
        }

        var history = new HistoryProjectionService(database);
        var run = Assert.Single(await history.ListRunsAsync());
        var artifact = Assert.Single(await history.ListArtifactsAsync());
        var episode = Assert.Single(await history.ListEpisodesAsync());
        Assert.Single(await history.ListWikiAsync());
        Assert.Equal("c1", HistoryProjectionService.ConversationIdForRun(run.Id));
        Assert.Equal("c1", HistoryProjectionService.ConversationIdForRun(
            artifact.RunId));
        Assert.Equal("c1", episode.SourceConversationId);
    }

    [Theory]
    [InlineData("codex:thread-1", "codex", "codex:thread-1", "thread-1")]
    [InlineData("thread-1", "codex", "thread-1", null)]
    [InlineData("custom:thread-1", null, "custom:thread-1", null)]
    public void HistoryConversationReferencesResolveAutomaticPrefixes(
        string reference,
        string? sourceAgent,
        string first,
        string? second)
    {
        var candidates = HistoryProjectionService.ConversationIdCandidates(
            reference,
            sourceAgent);
        Assert.Equal(first, candidates[0]);
        if (second is null)
        {
            Assert.Single(candidates);
        }
        else
        {
            Assert.Equal([first, second], candidates);
        }
    }

    [Fact]
    public async Task RepositoryGovernancePersistsAliasesCandidatesAndMergeProposals()
    {
        var database = new AIMemoryDatabase(Path.Combine(_root, "governance.db"));
        await database.InitializeAsync();
        var service = new RepositoryGovernanceService(database);
        var repoId = await service.ResolveRepoIdAsync(@"C:\repo", create: true);
        Assert.NotNull(repoId);
        var alias = await service.MergeAliasAsync(@"C:\repo", @"D:\old-repo");
        Assert.Equal(repoId, alias.RepoId);
        Assert.Equal(
            repoId,
            await service.ResolveRepoIdAsync(@"D:\old-repo"));
        var candidateId = await service.CreateMemoryCandidateAsync(
            @"D:\old-repo",
            "convention",
            "Use native UI",
            "Use WinUI 3",
            "Matches product requirements",
            1.5,
            "test");
        var candidate = Assert.Single(
            await service.ListCandidatesAsync(@"C:\repo", "pending_review"));
        Assert.Equal(candidateId, candidate.Id);
        Assert.Equal(1, candidate.Confidence);
        var repository = Assert.Single(
            await service.ListRepositoriesAsync());
        Assert.Equal(repoId, repository.Id);
        Assert.Equal(@"C:\repo", repository.Root);
        Assert.Equal(1, repository.PendingCandidates);

        await using (var connection = database.OpenConnection())
        {
            var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO approved_memories(
                  memory_id,repo_id,kind,title,value,usage_hint,status,
                  last_verified_at,created_from_candidate_id,created_at,
                  updated_at,freshness_status,freshness_score,verified_at,
                  verified_by)
                VALUES(
                  'memory',$repo,'convention','Native','WinUI','',
                  'active',$now,NULL,$now,$now,'fresh',1.0,$now,'test');
                """;
            insert.Parameters.AddWithValue("$repo", repoId);
            insert.Parameters.AddWithValue(
                "$now", DateTimeOffset.UtcNow.ToString("O"));
            await insert.ExecuteNonQueryAsync();
        }
        var proposal = await service.ProposeMemoryMergeAsync(
            @"C:\repo",
            candidateId,
            "memory",
            "Native UI",
            "Use WinUI 3",
            "Apply to desktop UI",
            "",
            "test");
        Assert.Equal("pending_review", proposal.Status);
        Assert.Equal(repoId, proposal.RepoId);
    }

    [Fact]
    public async Task ContinuationToolsCreateCheckpointHandoffAndFilterProjections()
    {
        var database = new AIMemoryDatabase(Path.Combine(_root, "continuation.db"));
        await database.InitializeAsync();
        var governance = new RepositoryGovernanceService(database);
        var repoId = await governance.ResolveRepoIdAsync(@"C:\repo", create: true);
        var now = DateTimeOffset.UtcNow.ToString("O");
        await using (var connection = database.OpenConnection())
        {
            var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO conversations VALUES(
                  'c1',$repo,'codex','source','task',$now,$now,NULL);
                INSERT INTO messages VALUES(
                  'm1','c1','user','continue',$now);
                INSERT INTO agent_runs VALUES(
                  'run:c1',$repo,'codex','task','active','summary',$now,NULL);
                INSERT INTO agent_runs VALUES(
                  'run:done',$repo,'codex','done','completed','done',$now,$now);
                INSERT INTO artifacts VALUES(
                  'a1','run:c1','patch','Patch','summary',NULL,NULL,
                  'verified',$now);
                INSERT INTO artifacts VALUES(
                  'a2','run:done','log','Log','summary',NULL,NULL,
                  'verified',$now);
                INSERT INTO wiki_pages VALUES(
                  'w1',$repo,'notes','Notes','body','active','[]','[]',
                  $now,NULL,$now,$now);
                """;
            insert.Parameters.AddWithValue("$repo", repoId);
            insert.Parameters.AddWithValue("$now", now);
            await insert.ExecuteNonQueryAsync();
        }
        var tools = new ContinuationToolService(
            database,
            new ConversationRepository(database),
            governance);
        var checkpoint = await tools.CreateCheckpointAsync(
            @"C:\repo",
            "c1",
            "codex",
            "Continue Windows parity",
            "codex resume c1",
            """{"source":"test"}""");
        Assert.Equal("Continue Windows parity", checkpoint.Summary);
        Assert.Equal("""{"source":"test"}""", checkpoint.MetadataJson);
        var handoff = await tools.ResumeFromCheckpointAsync(
            checkpoint.Id,
            "claude",
            "desktop");
        Assert.Equal("claude", handoff.ToAgent);
        Assert.Single(await tools.ListRunsAsync(@"C:\repo"));
        Assert.Equal(2, (await tools.ListArtifactsAsync(@"C:\repo")).Count);
        Assert.Single(await tools.ListWikiAsync(@"C:\repo"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            tools.CreateCheckpointAsync(
                @"C:\missing",
                "c1",
                "codex",
                "wrong repo",
                null,
                null));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            tools.CreateCheckpointAsync(
                @"C:\repo",
                "c1",
                "claude",
                "wrong agent",
                null,
                null));
    }

    [Fact]
    public async Task KnowledgeToolsRebuildWikiIndexConflictsAndEntityGraph()
    {
        var database = new AIMemoryDatabase(Path.Combine(_root, "knowledge.db"));
        await database.InitializeAsync();
        var governance = new RepositoryGovernanceService(database);
        var repoId = await governance.ResolveRepoIdAsync(@"C:\repo", create: true);
        var now = DateTimeOffset.UtcNow.ToString("O");
        await using (var connection = database.OpenConnection())
        {
            var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO conversations VALUES(
                  'c1',$repo,'codex','source','Conversation',$now,$now,NULL);
                INSERT INTO messages VALUES(
                  'm1','c1','user','continue parity',$now);
                INSERT INTO approved_memories(
                  memory_id,repo_id,kind,title,value,usage_hint,status,
                  last_verified_at,created_from_candidate_id,created_at,updated_at,
                  freshness_status,freshness_score,verified_at,verified_by)
                VALUES(
                  'mem1',$repo,'command','Build','dotnet test','before commit',
                  'active',$now,NULL,$now,$now,'fresh',1,$now,'user');
                INSERT INTO episodes VALUES(
                  'ep1',$repo,'Windows parity','Implemented','passed',$now,'c1');
                INSERT INTO memory_candidates VALUES(
                  'candidate1',$repo,'command','Build changed','dotnet test -c Release',
                  'keep CI aligned',0.9,'test','pending_review',$now,NULL);
                INSERT INTO memory_conflicts VALUES(
                  'conflict1',$repo,'candidate1','mem1','command changed',
                  'open',$now,NULL);
                INSERT INTO memory_entities VALUES(
                  'entity1',$repo,'WinUI','winui','framework',$now,$now);
                INSERT INTO memory_entity_links VALUES(
                  'link1',$repo,'entity1','memory','mem1','uses',$now);
                """;
            insert.Parameters.AddWithValue("$repo", repoId);
            insert.Parameters.AddWithValue("$now", now);
            await insert.ExecuteNonQueryAsync();
        }

        var knowledge = new KnowledgeProjectionService(database, governance);
        var pages = await knowledge.RebuildWikiAsync(@"C:\repo");
        Assert.Equal(2, pages.Count);
        Assert.Contains(pages, page => page.Slug == "command");
        Assert.Contains(pages, page => page.Slug == "episodes");

        var index = await knowledge.RebuildSearchIndexAsync(@"C:\repo");
        Assert.Equal(4, index.DocumentCount);
        Assert.Equal(index.DocumentCount, index.EmbeddingCount);
        Assert.Single(await knowledge.ListConflictsAsync(@"C:\repo", "open"));
        Assert.Empty(await knowledge.ListConflictsAsync(@"C:\repo", "resolved"));

        var graph = await knowledge.ListEntityGraphAsync(@"C:\repo", 25);
        Assert.Single(graph.Entities);
        Assert.Single(graph.Links);
        Assert.Equal("Build", graph.Links[0].SourceTitle);
    }

    [Fact]
    public void AgentCatalogKeepsDetectedEntriesBeforeMissingAndNeverEnablesMissing()
    {
        var bin = Path.Combine(_root, "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "goose.exe"), "");
        File.WriteAllText(Path.Combine(bin, "vibe.cmd"), "");
        File.WriteAllText(Path.Combine(bin, "opencode.cmd"), "");
        File.WriteAllText(Path.Combine(bin, "qodercli.cmd"), "");
        File.WriteAllText(Path.Combine(bin, "mimo.cmd"), "");
        File.WriteAllText(Path.Combine(bin, "aichat.cmd"), "");
        File.WriteAllText(Path.Combine(bin, "nanoclaw.exe"), "");
        File.WriteAllText(Path.Combine(bin, "gitclaw.cmd"), "");
        var statuses = new AgentCatalog(_root, [bin]).Detect();
        Assert.Equal(
        [
            "claude", "codex", "gemini", "antigravity", "opencode",
            "hermes", "zcode", "kimi", "cursor", "vscode", "copilot",
            "qwen", "amazonq", "factory", "windsurf", "kiro", "continue",
            "goose", "cline", "roo", "aider", "amp", "warp", "trae",
            "junie", "crush", "augment", "cody", "tabby", "openhands",
            "open-interpreter", "openclaw", "codebuddy", "devin", "vibe",
            "pi", "kilo", "plandex", "gptme", "mini-swe-agent",
            "google-agents-cli",
            "rovo-dev", "gitlab-duo", "grok-build", "jules",
            "alquimia", "auggie", "firebender", "forge", "ibm-bob",
            "iflow", "lingma", "oh-my-pi", "qoder", "shai",
            "swe-agent", "tabnine-cli", "zed",
            "deepagents-code", "mimo-code", "codebuff", "kode",
            "letta-code", "nanocoder", "ra-aid", "conductor", "waza",
            "langsmith-cli", "cortex-code", "cline-kanban",
            "aichat", "llm", "fabric", "shell-gpt", "elia", "ollama",
            "lm-studio", "llama-cpp", "tgpt", "crewai", "autogpt",
            "gptscript", "elizaos", "openai-cli",
            "huggingface-cli", "m365-agents-toolkit", "github-agentic-workflows",
            "neovate", "vtcode", "dexto", "nanobot", "zeroclaw",
            "picoclaw", "ironclaw", "nullclaw", "moltis",
            "opensquilla", "qodo", "coderabbit", "poolside",
            "command-code", "ante", "mentat",
            "claw-code", "coro", "nori-cli", "codemachine", "open-codex",
            "groq-code-cli", "devon", "g3", "mini-kode", "zot", "vibepod",
            "every-code", "claw-code-agent", "gitagent", "opendev", "qodex",
            "clawcodex", "tutti", "acpx", "cmux", "muxd", "muxel",
            "flowmux", "mcpjam", "zenflow", "void", "ruflo",
            "claurst", "agentty", "herdr",
            "smol-developer", "claude-engineer", "free-code", "forgecode",
            "autocoderover", "agentless", "codel", "open-harness", "octomind",
            "codex-infinity", "san-agent", "waveloom", "picocode", "qqcode",
            "keen-code", "smelt", "grinta", "zap-agent", "binharic", "darce",
            "claii", "nanoclaw", "clawith", "claw0", "gitclaw", "lionclaw",
            "fetchcoder", "crab-code", "openagent", "dvalincode", "lettabot",
            "oh-my-openagent",
        ], AgentCatalog.All.Select(value => value.Id).ToArray());
        var firstMissing = statuses
            .Select((status, index) => (status, index))
            .First(value => !value.status.IsDetected).index;
        Assert.Equal(8, firstMissing);
        Assert.Equal(["opencode", "goose", "vibe", "qoder", "mimo-code", "aichat", "nanoclaw", "gitclaw"], statuses
            .Take(firstMissing).Select(value => value.Id).ToArray());
        Assert.All(statuses.Take(firstMissing), value => Assert.True(value.IsDetected));
        Assert.All(statuses.Skip(firstMissing), value =>
        {
            Assert.False(value.IsDetected);
            Assert.False(value.IsIntegrated);
            Assert.Equal(AgentIntegrationState.Missing, value.State);
        });
        Assert.Equal(165, statuses.Count);

        var missingWithStaleConfiguration =
            AgentIntegrationStateService.ApplyConfigurationState(
                statuses.First(value => !value.IsDetected),
                true);
        Assert.False(missingWithStaleConfiguration.IsIntegrated);
        Assert.Equal(
            AgentIntegrationState.Missing,
            missingWithStaleConfiguration.State);
        Assert.Contains("当前不会启动", missingWithStaleConfiguration.Detail);

        var detectedWithConfiguration =
            AgentIntegrationStateService.ApplyConfigurationState(
                statuses.First(value => value.Id == "opencode"),
                true);
        Assert.True(detectedWithConfiguration.IsIntegrated);
        Assert.Equal(
            AgentIntegrationState.Integrated,
            detectedWithConfiguration.State);
    }

    [Fact]
    public void AgentCatalogRefreshesProcessPathWhenDetectionRuns()
    {
        var bin = Path.Combine(_root, "path-refresh-bin");
        Directory.CreateDirectory(bin);
        var previousPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", bin);
            var catalog = new AgentCatalog(_root);
            Assert.False(catalog.Detect().Single(value => value.Id == "opencode").IsDetected);

            File.WriteAllText(Path.Combine(bin, "opencode.cmd"), "");
            Assert.True(catalog.Detect().Single(value => value.Id == "opencode").IsDetected);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previousPath);
        }
    }

    [Fact]
    public void AgentCatalogDetectsInstalledDesktopAgentPath()
    {
        var windsurfPath = Path.Combine(
            _root,
            "AppData",
            "Local",
            "Programs",
            "Windsurf");
        Directory.CreateDirectory(windsurfPath);
        var kiroPath = Path.Combine(
            _root,
            "AppData",
            "Local",
            "Programs",
            "Kiro");
        Directory.CreateDirectory(kiroPath);
        var voidPath = Path.Combine(
            _root,
            "AppData",
            "Local",
            "Programs",
            "Void");
        Directory.CreateDirectory(voidPath);
        foreach (var agent in new[] { "claurst", "agentty", "herdr" })
        {
            Directory.CreateDirectory(Path.Combine(
                _root,
                "AppData",
                "Local",
                agent));
        }

        var statuses = new AgentCatalog(_root, []).Detect();
        var windsurf = statuses.Single(value => value.Id == "windsurf");
        var kiro = statuses.Single(value => value.Id == "kiro");
        var status = statuses.Single(value => value.Id == "void");
        var claurst = statuses.Single(value => value.Id == "claurst");
        var agentty = statuses.Single(value => value.Id == "agentty");
        var herdr = statuses.Single(value => value.Id == "herdr");

        Assert.Equal("windsurf", statuses[0].Id);
        Assert.True(windsurf.IsDetected);
        Assert.Equal(AgentIntegrationState.Detected, windsurf.State);
        Assert.True(kiro.IsDetected);
        Assert.Equal(AgentIntegrationState.Detected, kiro.State);
        Assert.True(status.IsDetected);
        Assert.False(status.IsIntegrated);
        Assert.Equal(AgentIntegrationState.Detected, status.State);
        Assert.True(claurst.IsDetected);
        Assert.True(agentty.IsDetected);
        Assert.True(herdr.IsDetected);
    }

    [Fact]
    public void AgentIntegrationInstallsRepairsAndRemovesOwnedConfiguration()
    {
        var home = Path.Combine(_root, "integration-home");
        var bin = Path.Combine(home, "bin");
        var helper = Path.Combine(home, "app", "aimemory-mcp.exe");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(Path.GetDirectoryName(helper)!);
        File.WriteAllText(Path.Combine(bin, "opencode.cmd"), "");
        File.WriteAllText(helper, "helper");
        var config = Path.Combine(
            home,
            ".config",
            "opencode",
            "opencode.json");
        var rules = Path.Combine(
            home,
            ".config",
            "opencode",
            "AGENTS.md");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        File.WriteAllText(config, """{"theme":"user-theme"}""");
        File.WriteAllText(rules, "# Existing user rules\n");
        var manager = new AgentIntegrationManager(home, helper, [bin]);

        var detected = manager.Detect()
            .First(value => value.Id == "opencode");
        Assert.True(detected.IsDetected);
        Assert.False(detected.IsIntegrated);
        manager.SetEnabled(detected, true);

        var enabled = manager.Detect()
            .First(value => value.Id == "opencode");
        Assert.True(enabled.IsIntegrated);
        Assert.Equal(AgentIntegrationState.Integrated, enabled.State);
        var configText = File.ReadAllText(config);
        Assert.Contains("\"theme\": \"user-theme\"", configText);
        Assert.Contains("\"aimemory\"", configText);
        Assert.Contains("\"aimemory_*\": true", configText);
        Assert.Contains("\"aimemory\": \"allow\"", configText);
        Assert.True(File.Exists(Path.Combine(
            home,
            ".config",
            "opencode",
            "skills",
            "aimemory",
            "SKILL.md")));
        Assert.Contains(
            "<!-- AIMEMORY-INTEGRATION:START -->",
            File.ReadAllText(rules));
        Assert.NotEmpty(Directory.GetFiles(
            Path.GetDirectoryName(config)!,
            "opencode.json.aimemory-backup-*"));

        manager.SetEnabled(enabled, false);

        var disabled = manager.Detect()
            .First(value => value.Id == "opencode");
        Assert.False(disabled.IsIntegrated);
        Assert.Equal(AgentIntegrationState.Detected, disabled.State);
        configText = File.ReadAllText(config);
        Assert.Contains("\"theme\": \"user-theme\"", configText);
        Assert.DoesNotContain("\"aimemory\"", configText);
        Assert.DoesNotContain("\"aimemory_*\"", configText);
        Assert.False(Directory.Exists(Path.Combine(
            home,
            ".config",
            "opencode",
            "skills",
            "aimemory")));
        var rulesText = File.ReadAllText(rules);
        Assert.Contains("# Existing user rules", rulesText);
        Assert.DoesNotContain("AIMEMORY-INTEGRATION", rulesText);
    }

    [Fact]
    public void EverySupportedAgentIntegrationRoundTrips()
    {
        var home = Path.Combine(_root, "all-integrations-home");
        var bin = Path.Combine(home, "bin");
        var helper = Path.Combine(home, "app", "aimemory-mcp.exe");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(Path.GetDirectoryName(helper)!);
        File.WriteAllText(helper, "helper");
        foreach (var descriptor in AgentCatalog.All.Where(
                     value => value.SupportsAutomaticIntegration))
        {
            File.WriteAllText(
                Path.Combine(bin, descriptor.Executables[0] + ".cmd"),
                "");
        }
        var manager = new AgentIntegrationManager(home, helper, [bin]);
        var supported = manager.Detect()
            .Where(value => value.IsIntegrationAvailable)
            .ToArray();

        Assert.Equal(16, supported.Length);
        foreach (var status in supported)
        {
            Assert.True(status.IsDetected);
            manager.SetEnabled(status, true);
        }
        var enabled = manager.Detect()
            .Where(value => value.IsIntegrationAvailable)
            .ToArray();
        Assert.All(enabled, status =>
        {
            Assert.True(status.IsIntegrated);
            Assert.Equal(AgentIntegrationState.Integrated, status.State);
        });

        foreach (var status in enabled)
        {
            manager.SetEnabled(status, false);
        }
        var disabled = manager.Detect()
            .Where(value => value.IsIntegrationAvailable)
            .ToArray();
        Assert.All(disabled, status =>
        {
            Assert.True(status.IsDetected);
            Assert.False(status.IsIntegrated);
            Assert.Equal(AgentIntegrationState.Detected, status.State);
        });
    }

    [Fact]
    public async Task TrashRoundTripRestoresConversationAndMessages()
    {
        var database = new AIMemoryDatabase(Path.Combine(_root, "trash.db"));
        await database.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        await using (var connection = database.OpenConnection())
        {
            var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO conversations VALUES(
                  'c1','repo','codex','source','title',$now,$now,NULL);
                INSERT INTO messages VALUES('m1','c1','user','hello',$now);
                """;
            insert.Parameters.AddWithValue("$now", now.ToString("O"));
            await insert.ExecuteNonQueryAsync();
        }
        var repository = new ConversationRepository(database);
        var conversation = Assert.Single(await repository.ListAsync());
        var trash = new TrashService(database, Path.Combine(_root, "trash"));
        var record = await trash.TrashAsync(conversation, 14);
        Assert.Equal(0, await repository.CountAsync());
        await trash.RestoreAsync(record);
        Assert.Equal(1, await repository.CountAsync());
        Assert.Single(await repository.ReadMessagesAsync("c1"));
    }

    [Fact]
    public async Task BulkTrashContinuesAfterFailureAndKeepsRecoverableCopies()
    {
        var database = new AIMemoryDatabase(
            Path.Combine(_root, "trash-bulk.db"));
        await database.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        await using (var connection = database.OpenConnection())
        {
            var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO conversations VALUES(
                  'bulk-1','repo','codex','source-1','first',$now,$now,NULL);
                INSERT INTO conversations VALUES(
                  'bulk-2','repo','claude','source-2','second',$now,$now,NULL);
                INSERT INTO messages VALUES(
                  'bulk-m1','bulk-1','user','first message',$now);
                INSERT INTO messages VALUES(
                  'bulk-m2','bulk-2','user','second message',$now);
                """;
            insert.Parameters.AddWithValue("$now", now.ToString("O"));
            await insert.ExecuteNonQueryAsync();
        }
        var repository = new ConversationRepository(database);
        var conversations = await repository.ListAsync();
        var missing = conversations[0] with
        {
            Id = "missing",
            SourceConversationId = "missing",
        };
        var trash = new TrashService(
            database,
            Path.Combine(_root, "trash-bulk"));

        var result = await trash.TrashManyAsync(
            [conversations[0], missing, conversations[1]],
            14);

        Assert.Equal(2, result.Moved);
        Assert.Equal(["missing"], result.FailedConversationIds);
        Assert.Equal(0, await repository.CountAsync());
        var records = await trash.ListAsync();
        Assert.Equal(2, records.Count);
        foreach (var record in records)
        {
            await trash.RestoreAsync(record);
        }
        Assert.Equal(2, await repository.CountAsync());
        Assert.Single(await repository.ReadMessagesAsync("bulk-1"));
        Assert.Single(await repository.ReadMessagesAsync("bulk-2"));
    }

    [Fact]
    public async Task TrashRestoreReadsLegacyMessageOnlyEnvelope()
    {
        var database = new AIMemoryDatabase(
            Path.Combine(_root, "trash-legacy.db"));
        await database.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        await using (var connection = database.OpenConnection())
        {
            var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO conversations VALUES(
                  'legacy','repo','codex','source','legacy title',
                  $now,$now,NULL);
                INSERT INTO messages VALUES(
                  'legacy-message','legacy','user','legacy content',$now);
                """;
            insert.Parameters.AddWithValue("$now", now.ToString("O"));
            await insert.ExecuteNonQueryAsync();
        }
        var repository = new ConversationRepository(database);
        var conversation = Assert.Single(await repository.ListAsync());
        var messages = await repository.ReadMessagesAsync(conversation.Id);
        var trash = new TrashService(
            database, Path.Combine(_root, "trash-legacy"));
        var record = await trash.TrashAsync(conversation, 14);
        var legacyEnvelope = new
        {
            Record = record,
            Conversation = conversation,
            Messages = messages,
        };
        await File.WriteAllTextAsync(
            record.RecordPath,
            JsonSerializer.Serialize(
                legacyEnvelope,
                new JsonSerializerOptions
                {
                    PropertyNamingPolicy =
                        JsonNamingPolicy.SnakeCaseLower,
                }));

        await trash.RestoreAsync(record);

        var restored = await repository.ExportAsync("legacy");
        Assert.Equal("legacy content", Assert.Single(restored.Messages).Content);
    }

    [Fact]
    public async Task TrashPurgesExpiredRecordsAndCanBeEmptied()
    {
        var database = new AIMemoryDatabase(Path.Combine(_root, "trash-expiry.db"));
        await database.InitializeAsync();
        var createdAt = DateTimeOffset.Parse("2026-07-01T00:00:00Z");
        await using (var connection = database.OpenConnection())
        {
            var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO conversations VALUES(
                  'expired','repo','codex','source','old',$now,$now,NULL);
                """;
            insert.Parameters.AddWithValue("$now", createdAt.ToString("O"));
            await insert.ExecuteNonQueryAsync();
        }
        var repository = new ConversationRepository(database);
        var trashDirectory = Path.Combine(_root, "trash-expiry");
        var clock = createdAt;
        var trash = new TrashService(database, trashDirectory, () => clock);
        await trash.TrashAsync(
            Assert.Single(await repository.ListAsync()), 1);
        clock = createdAt.AddDays(2);
        Assert.Empty(await trash.ListAsync());
        Assert.Empty(Directory.EnumerateFiles(trashDirectory, "*.json"));

        await using (var connection = database.OpenConnection())
        {
            var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO conversations VALUES(
                  'keep','repo','codex','source','keep',$now,$now,NULL);
                """;
            insert.Parameters.AddWithValue("$now", clock.ToString("O"));
            await insert.ExecuteNonQueryAsync();
        }
        await trash.TrashAsync(
            Assert.Single(await repository.ListAsync()), 14);
        Assert.Equal(1, await trash.EmptyAsync());
        Assert.Empty(await trash.ListAsync());
    }

    [Fact]
    public async Task WebDavSyncUploadsOnceThenSkipsUnchangedConversation()
    {
        var database = new AIMemoryDatabase(Path.Combine(_root, "webdav.db"));
        await database.InitializeAsync();
        var now = DateTimeOffset.UtcNow.ToString("O");
        await using (var connection = database.OpenConnection())
        {
            var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO conversations VALUES(
                  'c1','repo','codex','source','title',$now,$now,NULL);
                INSERT INTO messages VALUES('m1','c1','user','hello',$now);
                """;
            insert.Parameters.AddWithValue("$now", now);
            await insert.ExecuteNonQueryAsync();
        }
        var handler = new MemoryWebDavHandler();
        var repository = new ConversationRepository(database);
        var service = new WebDavService(
            repository,
            new HttpClient(handler));
        var root = new Uri("https://dav.example.test/chatmem/");

        var first = await service.SyncAsync(root, null, null);
        Assert.Equal(1, first.Uploaded);
        Assert.Equal(0, first.Skipped);

        var second = await service.SyncAsync(root, null, null);
        Assert.Equal(0, second.Uploaded);
        Assert.Equal(1, second.Skipped);
    }

    [Fact]
    public void WebDavSemanticDigestMatchesSharedProtocolVector()
    {
        using var input = JsonDocument.Parse(
            """{"z":{"nested":[true,null,"x"]},"alpha":1}""");
        var detail = new WebDavConversationDetail(
            "vector-1", "codex", "/tmp/semantic",
            "2026-07-23T10:00:00Z", "2026-07-23T11:00:00Z",
            "跨平台", null, "ignored resume",
            [
                new WebDavMessage(
                    "m-1", "2026-07-23T10:30:00Z", "user", "hello 🌿",
                    [
                        new WebDavToolCall(
                            "tool-1", "shell", input.RootElement.Clone(), null,
                            "completed"),
                    ],
                    new Dictionary<string, JsonElement>
                    {
                        ["ignored"] = JsonSerializer.SerializeToElement("metadata"),
                    }),
            ],
            [
                new WebDavFileChange(
                    "/tmp/a.swift", "modified", "2026-07-23T10:31:00Z", "m-1"),
            ]);

        var equivalentAfterStoreRoundTrip = detail with
        {
            ResumeCommand = "another generated command",
            Messages =
            [
                detail.Messages[0] with
                {
                    Metadata = new Dictionary<string, JsonElement>(),
                },
            ],
        };

        const string expected =
            "aimemory-conversation-v1:41c37b3f58708d33d64d27c22a6f37ac74559d75b7d488b3c574f1a9f63db550";
        Assert.Equal(expected, WebDavService.SemanticDigest(detail));
        Assert.Equal(expected, WebDavService.SemanticDigest(equivalentAfterStoreRoundTrip));
    }

    [Fact]
    public async Task WebDavSyncSkipsSemanticEquivalentSerializerVariant()
    {
        var database = new AIMemoryDatabase(Path.Combine(_root, "webdav-semantic.db"));
        await database.InitializeAsync();
        const string updatedAt = "2026-07-23T11:00:00Z";
        await using (var connection = database.OpenConnection())
        {
            var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO conversations VALUES(
                  'semantic','repo','codex','source','same logical content',$now,$now,NULL);
                INSERT INTO messages VALUES('semantic-message','semantic','user','hello',$now);
                """;
            insert.Parameters.AddWithValue("$now", updatedAt);
            await insert.ExecuteNonQueryAsync();
        }
        var repository = new ConversationRepository(database);
        var detail = await repository.ExportAsync("semantic");
        var compactOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = false,
        };
        var remotePayload = JsonSerializer.SerializeToUtf8Bytes(detail, compactOptions);
        var prettyPayload = JsonSerializer.SerializeToUtf8Bytes(detail, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = true,
        });
        Assert.False(remotePayload.AsSpan().SequenceEqual(prettyPayload));

        var idFileName = Convert.ToBase64String(Encoding.UTF8.GetBytes(detail.Id))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".json";
        var entry = new WebDavManifestEntry(
            detail.SourceAgent, detail.Id,
            $"conversations/{detail.SourceAgent}/{idFileName}",
            detail.UpdatedAt, "different-serializer-byte-hash",
            WebDavService.SemanticDigest(detail));
        var manifest = JsonSerializer.SerializeToUtf8Bytes(
            new WebDavManifest(2, updatedAt, [entry]), compactOptions);
        var handler = new MemoryWebDavHandler();
        handler.Seed("/chatmem/manifest.json", manifest);
        handler.Seed($"/chatmem/conversations/{detail.SourceAgent}/{idFileName}", remotePayload);
        var service = new WebDavService(repository, new HttpClient(handler));

        var result = await service.SyncAsync(
            new Uri("https://dav.example.test/chatmem/"), null, null);

        Assert.Equal(0, result.Uploaded);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(0, handler.ConversationPutCount);
        Assert.Equal(0, handler.ManifestPutCount);
    }

    [Fact]
    public async Task WebDavSyncLegacyEqualTimestampDifferentContentUploadsLocal()
    {
        var database = new AIMemoryDatabase(Path.Combine(_root, "webdav-legacy-conflict.db"));
        await database.InitializeAsync();
        const string updatedAt = "2026-07-23T11:00:00Z";
        await using (var connection = database.OpenConnection())
        {
            var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO conversations VALUES(
                  'legacy-conflict','repo','codex','source','local content',$now,$now,NULL);
                INSERT INTO messages VALUES(
                  'legacy-conflict-message','legacy-conflict','user','local message',$now);
                """;
            insert.Parameters.AddWithValue("$now", updatedAt);
            await insert.ExecuteNonQueryAsync();
        }
        var repository = new ConversationRepository(database);
        var local = await repository.ExportAsync("legacy-conflict");
        var remote = local with
        {
            Summary = "remote content",
            Messages =
            [
                local.Messages[0] with { Content = "remote message" },
            ],
        };
        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = false,
        };
        var remotePayload = JsonSerializer.SerializeToUtf8Bytes(remote, jsonOptions);
        var idFileName = Convert.ToBase64String(Encoding.UTF8.GetBytes(local.Id))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".json";
        var entry = new WebDavManifestEntry(
            local.SourceAgent, local.Id,
            $"conversations/{local.SourceAgent}/{idFileName}",
            local.UpdatedAt, null);
        var manifest = JsonSerializer.SerializeToUtf8Bytes(
            new WebDavManifest(1, updatedAt, [entry]), jsonOptions);
        var handler = new MemoryWebDavHandler();
        handler.Seed("/chatmem/manifest.json", manifest);
        handler.Seed($"/chatmem/conversations/{local.SourceAgent}/{idFileName}", remotePayload);
        var service = new WebDavService(repository, new HttpClient(handler));

        var result = await service.SyncAsync(
            new Uri("https://dav.example.test/chatmem/"), null, null);

        Assert.Equal(1, result.Uploaded);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(1, handler.ConversationPutCount);
        Assert.Equal(1, handler.ManifestPutCount);
    }

    [Fact]
    public async Task NativeHistoryImportCopiesAllEightSourcesReadOnly()
    {
        var home = Path.Combine(_root, "home");
        var claudeProject = Path.Combine(home, ".claude", "projects", "repo");
        Directory.CreateDirectory(claudeProject);
        var claudePath = Path.Combine(claudeProject, "claude-1.jsonl");
        await File.WriteAllLinesAsync(claudePath,
        [
            """{"type":"user","cwd":"C:\\repo","timestamp":"2026-07-01T01:00:00Z","message":{"role":"user","content":"Claude question"}}""",
            """{"type":"assistant","cwd":"C:\\repo","timestamp":"2026-07-01T01:01:00Z","message":{"role":"assistant","content":[{"type":"text","text":"Claude answer"}]}}""",
        ]);

        var codexRoot = Path.Combine(home, ".codex");
        var rolloutDirectory = Path.Combine(codexRoot, "sessions", "2026", "07");
        Directory.CreateDirectory(rolloutDirectory);
        var rolloutPath = Path.Combine(rolloutDirectory, "codex-1.jsonl");
        await File.WriteAllLinesAsync(rolloutPath,
        [
            """{"timestamp":"2026-07-02T01:00:00Z","type":"session_meta","payload":{"cwd":"C:\\repo"}}""",
            """{"timestamp":"2026-07-02T01:01:00Z","type":"event_msg","payload":{"type":"user_message","message":"Codex question"}}""",
            """{"timestamp":"2026-07-02T01:02:00Z","type":"event_msg","payload":{"type":"agent_message","message":"Codex answer"}}""",
        ]);
        var statePath = Path.Combine(codexRoot, "state_5.sqlite");
        await using (var state = new SqliteConnection($"Data Source={statePath}"))
        {
            await state.OpenAsync();
            var create = state.CreateCommand();
            create.CommandText = """
                CREATE TABLE threads(
                  id TEXT,rollout_path TEXT,cwd TEXT,title TEXT,
                  created_at INTEGER,updated_at INTEGER,source TEXT);
                INSERT INTO threads VALUES(
                  'codex-1',$path,'C:\repo','Codex title',
                  1782954000,1782954120,NULL);
                """;
            create.Parameters.AddWithValue("$path", rolloutPath);
            await create.ExecuteNonQueryAsync();
        }

        var geminiChats = Path.Combine(home, ".gemini", "tmp", "hash", "chats");
        Directory.CreateDirectory(geminiChats);
        var geminiPath = Path.Combine(geminiChats, "gemini-1.json");
        await File.WriteAllTextAsync(
            geminiPath,
            """
            {
              "sessionId":"gemini-1",
              "projectPath":"C:\\repo",
              "startTime":"2026-07-03T01:00:00Z",
              "lastUpdated":"2026-07-03T01:02:00Z",
              "messages":[
                {"id":"g1","type":"user","content":"Gemini question","timestamp":"2026-07-03T01:00:00Z"},
                {"id":"g2","type":"gemini","content":"Gemini answer","timestamp":"2026-07-03T01:02:00Z"}
              ]
            }
            """);

        var hermesRoot = Path.Combine(home, ".hermes");
        Directory.CreateDirectory(hermesRoot);
        var hermesPath = Path.Combine(hermesRoot, "state.db");
        await using (var hermes = new SqliteConnection(
                         $"Data Source={hermesPath}"))
        {
            await hermes.OpenAsync();
            var create = hermes.CreateCommand();
            create.CommandText = """
                CREATE TABLE sessions(
                  id TEXT,title TEXT,started_at REAL,ended_at REAL,
                  cwd TEXT,archived INTEGER);
                CREATE TABLE messages(
                  id TEXT,session_id TEXT,role TEXT,content TEXT,
                  tool_calls TEXT,tool_name TEXT,timestamp REAL,active INTEGER);
                INSERT INTO sessions VALUES(
                  'hermes-1','Hermes title',1783050000,1783050060,
                  'C:\repo',0);
                INSERT INTO messages VALUES(
                  'hm1','hermes-1','user','Hermes question',
                  NULL,NULL,1783050000,1);
                INSERT INTO messages VALUES(
                  'hm2','hermes-1','assistant','Hermes answer',
                  NULL,NULL,1783050060,1);
                """;
            await create.ExecuteNonQueryAsync();
        }

        var kimiSession = Path.Combine(
            home, ".kimi-code", "sessions", "workspace", "kimi-1");
        var kimiAgent = Path.Combine(kimiSession, "agents", "main");
        Directory.CreateDirectory(kimiAgent);
        var kimiState = Path.Combine(kimiSession, "state.json");
        await File.WriteAllTextAsync(
            kimiState,
            """{"workDir":"C:\\repo","createdAt":"2026-07-04T01:00:00Z","updatedAt":"2026-07-04T01:02:00Z","title":"Kimi title"}""");
        var kimiWire = Path.Combine(kimiAgent, "wire.jsonl");
        await File.WriteAllLinesAsync(kimiWire,
        [
            """{"time":1783136400000,"type":"turn.prompt","input":[{"type":"text","text":"Kimi question"}]}""",
            """{"time":1783136460000,"type":"context.append_loop_event","event":{"type":"content.part","part":{"type":"text","text":"Kimi answer"}}}""",
        ]);

        var antigravitySession = Path.Combine(
            home, ".gemini", "antigravity", "brain", "anti-1");
        var antigravityLogs = Path.Combine(
            antigravitySession, ".system_generated", "logs");
        Directory.CreateDirectory(antigravityLogs);
        var antigravityTranscript = Path.Combine(
            antigravityLogs, "transcript.jsonl");
        await File.WriteAllLinesAsync(antigravityTranscript,
        [
            """{"source":"USER_EXPLICIT","content":"<USER_REQUEST>Antigravity question</USER_REQUEST>","created_at":"2026-07-05T01:00:00Z"}""",
            """{"source":"MODEL","content":"Antigravity answer","created_at":"2026-07-05T01:01:00Z"}""",
        ]);

        var zcodeProfile = Path.Combine(
            home, ".zcode", "v2", "sessions", "default");
        Directory.CreateDirectory(zcodeProfile);
        var zcodePath = Path.Combine(zcodeProfile, "zcode-1.json");
        await File.WriteAllTextAsync(
            zcodePath,
            """
            {
              "meta":{
                "provider":"codex",
                "taskId":"zcode-1",
                "workspacePath":"C:\\repo",
                "createdAt":1783222800000,
                "updatedAt":1783222860000,
                "title":"ZCode title"
              },
              "messages":[
                {"role":"user","content":"ZCode question","timestamp":1783222800000},
                {"role":"assistant","content":"ZCode answer","timestamp":1783222860000}
              ]
            }
            """);

        var openCodeRoot = Path.Combine(
            home, ".local", "share", "opencode");
        Directory.CreateDirectory(openCodeRoot);
        var openCodePath = Path.Combine(openCodeRoot, "opencode.db");
        await using (var openCode = new SqliteConnection(
                         $"Data Source={openCodePath}"))
        {
            await openCode.OpenAsync();
            var create = openCode.CreateCommand();
            create.CommandText = """
                CREATE TABLE session(
                  id TEXT,directory TEXT,title TEXT,time_created INTEGER,
                  time_updated INTEGER,time_archived INTEGER);
                CREATE TABLE message(
                  id TEXT,session_id TEXT,time_created INTEGER,data TEXT);
                CREATE TABLE part(
                  id TEXT,session_id TEXT,message_id TEXT,
                  time_created INTEGER,data TEXT);
                INSERT INTO session VALUES(
                  'opencode-1','C:\repo','OpenCode title',
                  1783309200000,1783309260000,NULL);
                INSERT INTO message VALUES(
                  'om1','opencode-1',1783309200000,'{"role":"user"}');
                INSERT INTO message VALUES(
                  'om2','opencode-1',1783309260000,'{"role":"assistant"}');
                INSERT INTO part VALUES(
                  'op1','opencode-1','om1',1783309200000,
                  '{"type":"text","text":"OpenCode question"}');
                INSERT INTO part VALUES(
                  'op2','opencode-1','om2',1783309260000,
                  '{"type":"text","text":"OpenCode answer"}');
                """;
            await create.ExecuteNonQueryAsync();
        }

        var database = new AIMemoryDatabase(Path.Combine(_root, "history.db"));
        await database.InitializeAsync();
        var repository = new ConversationRepository(database);
        var report = await new NativeHistoryImportService(repository, home)
            .ImportAllAsync();
        Assert.Equal(8, report.Total);
        Assert.Empty(report.Warnings);
        var conversations = await repository.ListAsync();
        Assert.Equal(8, conversations.Count);
        Assert.Contains(conversations, value => value.SourceAgent == "claude");
        Assert.Contains(conversations, value => value.SourceAgent == "codex");
        Assert.Contains(conversations, value => value.SourceAgent == "gemini");
        Assert.Contains(conversations, value => value.SourceAgent == "hermes");
        Assert.Contains(conversations, value => value.SourceAgent == "kimi");
        Assert.Contains(conversations, value => value.SourceAgent == "antigravity");
        Assert.Contains(conversations, value => value.SourceAgent == "opencode");
        Assert.Contains(conversations, value => value.SourceAgent == "zcode");
        Assert.Equal(
            "Claude question",
            (await repository.ReadMessagesAsync("claude-1")).First().Content);

        Assert.True(File.Exists(claudePath));
        Assert.True(File.Exists(rolloutPath));
        Assert.True(File.Exists(geminiPath));
        Assert.True(File.Exists(hermesPath));
        Assert.True(File.Exists(kimiWire));
        Assert.True(File.Exists(antigravityTranscript));
        Assert.True(File.Exists(openCodePath));
        Assert.True(File.Exists(zcodePath));
    }

    [Fact]
    public async Task WorkbenchInsightsExposeSignalsCleanupAndRecommendation()
    {
        var database = new AIMemoryDatabase(
            Path.Combine(_root, "workbench-insights.db"));
        await database.InitializeAsync();
        var now = DateTimeOffset.Parse("2026-07-29T12:00:00Z");
        await using (var connection = database.OpenConnection())
        {
            var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO repos VALUES(
                  'repo','C:\repo','fingerprint',NULL,'main',$now,$now);
                INSERT INTO conversations VALUES
                  ('latest','repo','Claude','latest','Latest file change',
                   $recent,$now,NULL),
                  ('long','repo','Gemini','long','Long conversation',
                   $recent,$recent,NULL),
                  ('favorite','repo','Kimi','favorite','Favorite conversation',
                   $recent,$recent,NULL),
                  ('cleanup','repo','Codex','cleanup','Old low signal',
                   $old,$old,NULL);
                INSERT INTO file_changes VALUES(
                  'file-1','latest','message-file','Program.cs','modified',$now);
                INSERT INTO messages VALUES
                  ('long-01','long','user','1',$recent),
                  ('long-02','long','assistant','2',$recent),
                  ('long-03','long','user','3',$recent),
                  ('long-04','long','assistant','4',$recent),
                  ('long-05','long','user','5',$recent),
                  ('long-06','long','assistant','6',$recent),
                  ('long-07','long','user','7',$recent),
                  ('long-08','long','assistant','8',$recent),
                  ('long-09','long','user','9',$recent),
                  ('long-10','long','assistant','10',$recent),
                  ('long-11','long','user','11',$recent),
                  ('long-12','long','assistant','12',$recent),
                  ('cleanup-1','cleanup','user','old',$old);
                INSERT INTO approved_memories VALUES(
                  'memory','repo','rule','Rule','Value','Use it','active',
                  NULL,NULL,$now,$now,'fresh',1.0,$now,'test');
                INSERT INTO memory_candidates VALUES(
                  'candidate','repo','rule','Candidate','Value','Why',
                  0.8,'test','pending_review',$now,NULL);
                INSERT INTO wiki_pages VALUES(
                  'wiki','repo','overview','Overview','Body','active',
                  '[]','[]',$now,NULL,$now,$now);
                """;
            insert.Parameters.AddWithValue("$now", now.ToString("O"));
            insert.Parameters.AddWithValue(
                "$recent",
                now.AddDays(-1).ToString("O"));
            insert.Parameters.AddWithValue(
                "$old",
                now.AddDays(-120).ToString("O"));
            await insert.ExecuteNonQueryAsync();
        }
        var favorite = new FavoriteConversationSnapshot(
            "favorite",
            "Kimi",
            "Favorite conversation",
            @"C:\repo",
            now.AddDays(-1));
        var service = new WorkbenchInsightService(database);

        var result = await service.LoadAsync(
            new Dictionary<string, FavoriteConversationSnapshot>
            {
                ["Kimi:favorite"] = favorite,
            },
            now);

        Assert.Equal(1, result.FavoriteCount);
        Assert.Equal(1, result.ApprovedMemoryCount);
        Assert.Equal(1, result.PendingCandidateCount);
        Assert.Equal(1, result.WikiPageCount);
        Assert.Equal("Codex", result.RecommendedAgent);
        Assert.True(result.RecommendationUsesFileChanges);
        var unavailableRecommendation = await service.LoadAsync(
            new Dictionary<string, FavoriteConversationSnapshot>
            {
                ["Kimi:favorite"] = favorite,
            },
            now,
            availableAgentIds: new HashSet<string>(
                ["claude"],
                StringComparer.OrdinalIgnoreCase));
        Assert.Equal("", unavailableRecommendation.RecommendedAgent);
        Assert.Equal(
            ["long", "latest", "favorite"],
            result.HighSignalConversations
                .Select(value => value.Conversation.Id));
        Assert.Equal(
            "cleanup",
            Assert.Single(result.CleanupCandidates).Conversation.Id);
    }

    [Fact]
    public async Task MemoryGovernanceApproveEditRetireAndReverifyAreRealWrites()
    {
        var database = new AIMemoryDatabase(Path.Combine(_root, "memory.db"));
        await database.InitializeAsync();
        await using (var connection = database.OpenConnection())
        {
            var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO memory_candidates(
                  candidate_id,repo_id,kind,summary,value,why_it_matters,
                  confidence,proposed_by,status,created_at,reviewed_at)
                VALUES(
                  'candidate-1','repo','rule','Use tests','Run tests',
                  'Prevents regressions',0.9,'test','pending_review',$now,NULL);
                INSERT INTO evidence_refs VALUES(
                  'evidence-1','candidate','candidate-1',NULL,NULL,NULL,NULL,
                  'A previous release regressed without tests.',$now);
                INSERT INTO memory_merge_proposals VALUES(
                  'proposal-1','repo','candidate-1','existing-memory',
                  'Unified test rule','Run targeted and full tests',
                  'Before release','Review scope','test','pending_review',
                  $now,$now);
                INSERT INTO memory_conflicts VALUES(
                  'conflict-1','repo','candidate-1','existing-memory',
                  'The commands differ.','open',$now,NULL);
                """;
            insert.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            await insert.ExecuteNonQueryAsync();
        }
        var service = new MemoryGovernanceService(database);
        var pending = Assert.Single(await service.ListCandidatesAsync());
        Assert.Equal(
            ["A previous release regressed without tests."],
            pending.EvidenceRefs);
        Assert.Contains("Unified test rule", pending.MergeSuggestion);
        Assert.Equal("The commands differ.", pending.ConflictSuggestion);
        await service.ApproveCandidateAsync(
            "candidate-1", "Test rule", "Run all tests", "Before release");
        Assert.Empty(await service.ListCandidatesAsync());
        var approved = Assert.Single(await service.ListApprovedAsync());
        Assert.Equal("Test rule", approved.Title);
        await service.UpdateApprovedAsync(
            approved.Id, "Updated rule", "Run targeted and full tests", "Before release");
        Assert.Equal(
            "Updated rule",
            Assert.Single(await service.ListApprovedAsync()).Title);
        await service.SetApprovedStateAsync(approved.Id, false);
        Assert.Empty(await service.ListApprovedAsync());
        await service.SetApprovedStateAsync(approved.Id, true);
        Assert.Equal(
            "fresh",
            Assert.Single(await service.ListApprovedAsync()).FreshnessStatus);
    }

    [Fact]
    public async Task MemoryGovernanceBulkReviewIsRepositoryScoped()
    {
        var database = new AIMemoryDatabase(
            Path.Combine(_root, "memory-bulk-review.db"));
        await database.InitializeAsync();
        await using (var connection = database.OpenConnection())
        {
            var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO memory_candidates(
                  candidate_id,repo_id,kind,summary,value,why_it_matters,
                  confidence,proposed_by,status,created_at,reviewed_at)
                VALUES
                  ('candidate-a','repo-a','rule','A','A','A',0.9,'test',
                   'pending_review',$now,NULL),
                  ('candidate-b','repo-b','rule','B','B','B',0.8,'test',
                   'pending_review',$now,NULL);
                """;
            insert.Parameters.AddWithValue(
                "$now",
                DateTimeOffset.UtcNow.ToString("O"));
            await insert.ExecuteNonQueryAsync();
        }
        var service = new MemoryGovernanceService(database);
        Assert.Equal(
            1,
            await service.ReviewAllPendingAsync("reject", "repo-a"));
        var remaining = Assert.Single(
            await service.ListCandidatesAsync());
        Assert.Equal("repo-b", remaining.RepoId);
    }

    [Fact]
    public async Task CheckpointPromotesToHandoffAndCanBeConsumed()
    {
        var database = new AIMemoryDatabase(Path.Combine(_root, "recovery.db"));
        await database.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        await using (var connection = database.OpenConnection())
        {
            var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO conversations VALUES(
                  'c1','repo','codex','source','Continue Windows work',$now,$now,NULL);
                INSERT INTO messages VALUES('m1','c1','user','hello',$now);
                """;
            insert.Parameters.AddWithValue("$now", now.ToString("O"));
            await insert.ExecuteNonQueryAsync();
        }
        var conversation = Assert.Single(
            await new ConversationRepository(database).ListAsync());
        var service = new RecoveryService(database);
        var checkpoint = await service.CreateCheckpointAsync(conversation, 1);
        Assert.Equal("codex resume c1", checkpoint.ResumeCommand);
        var handoff = await service.CreateHandoffAsync(checkpoint, "claude");
        Assert.Equal("claude", handoff.ToAgent);
        Assert.Equal(checkpoint.Id, handoff.CheckpointId);
        await service.MarkHandoffConsumedAsync(handoff.Id);
        Assert.Equal(
            "consumed",
            Assert.Single(await service.ListHandoffsAsync()).Status);
    }

    [Fact]
    public async Task LocalFolderSyncWritesOnceThenSkipsUnchangedConversation()
    {
        var database = new AIMemoryDatabase(Path.Combine(_root, "local-sync.db"));
        await database.InitializeAsync();
        var now = DateTimeOffset.UtcNow.ToString("O");
        await using (var connection = database.OpenConnection())
        {
            var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO conversations VALUES(
                  'local-1','repo','codex','source','title',$now,$now,NULL);
                INSERT INTO messages VALUES('local-m1','local-1','user','hello',$now);
                """;
            insert.Parameters.AddWithValue("$now", now);
            await insert.ExecuteNonQueryAsync();
        }
        var folder = Path.Combine(_root, "shared");
        var service = new LocalFolderSyncService(
            new ConversationRepository(database));
        var first = await service.SyncAsync(folder);
        Assert.Equal(1, first.Uploaded);
        Assert.Equal(0, first.Skipped);
        var canonicalFile = Path.Combine(
            folder, "conversations", "codex", LocalSyncFilename("local-1"));
        Assert.True(File.Exists(canonicalFile));
        Assert.False(File.Exists(Path.Combine(
            folder, "AIMemorySync", "conversations", "codex",
            LocalSyncFilename("local-1"))));

        var second = await service.SyncAsync(folder);
        Assert.Equal(0, second.Uploaded);
        Assert.Equal(1, second.Skipped);
    }

    [Fact]
    public async Task LocalFolderSyncScansCanonicalAndLegacyPayloadsWithoutManifest()
    {
        var database = new AIMemoryDatabase(Path.Combine(
            _root, "local-sync-cross-platform.db"));
        await database.InitializeAsync();
        var repository = new ConversationRepository(database);
        var folder = Path.Combine(_root, "shared-cross-platform");
        var canonical = Path.Combine(folder, "conversations", "codex");
        var legacy = Path.Combine(
            folder, "AIMemorySync", "conversations", "codex");
        Directory.CreateDirectory(canonical);
        Directory.CreateDirectory(legacy);

        var canonicalId = "swift-canonical";
        var canonicalData = Encoding.UTF8.GetBytes(
            SwiftStyleSyncPayload(canonicalId));
        await File.WriteAllBytesAsync(Path.Combine(
            canonical, LocalSyncFilename(canonicalId)), canonicalData);
        // Duplicate payload in the legacy layout must resolve to one logical
        // conversation rather than import twice.
        await File.WriteAllBytesAsync(Path.Combine(
            legacy, LocalSyncFilename(canonicalId)), canonicalData);
        var legacyId = "legacy-windows";
        await File.WriteAllTextAsync(
            Path.Combine(legacy, LocalSyncFilename(legacyId)),
            SwiftStyleSyncPayload(legacyId));

        var service = new LocalFolderSyncService(repository);
        var first = await service.SyncAsync(folder);
        Assert.Equal(0, first.Uploaded);
        Assert.Equal(2, first.Downloaded);
        Assert.Equal(2, await repository.CountAsync());
        Assert.True(File.Exists(Path.Combine(folder, "manifest.json")));

        var second = await service.SyncAsync(folder);
        Assert.Equal(0, second.Uploaded);
        Assert.Equal(0, second.Downloaded);
        Assert.Equal(2, second.Skipped);
        Assert.Equal(canonicalData, await File.ReadAllBytesAsync(Path.Combine(
            canonical, LocalSyncFilename(canonicalId))));
    }

    [Fact]
    public void LocalFolderSyncSemanticHashMatchesCrossPlatformFixture()
    {
        var method = typeof(LocalFolderSyncService).GetMethod(
            "SemanticHash",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var actual = (string?)method!.Invoke(
            null,
            [LocalFolderSemanticFixture()]);
        Assert.Equal(
            "2e7f520d598623953fcf41fb1ab39b49b1644e1a3401efeb7271699b7807ff16",
            actual);
    }

    [Fact]
    public async Task LocalFolderSyncIgnoresManifestPathsAndAgentDirectoryMismatches()
    {
        var database = new AIMemoryDatabase(Path.Combine(
            _root, "local-sync-traversal.db"));
        await database.InitializeAsync();
        var repository = new ConversationRepository(database);
        var folder = Path.Combine(_root, "shared-traversal");
        var outside = Path.Combine(_root, "outside.json");
        var outsideData = Encoding.UTF8.GetBytes(
            SwiftStyleSyncPayload("outside"));
        await File.WriteAllBytesAsync(outside, outsideData);
        var mismatchedFolder = Path.Combine(folder, "conversations", "codex");
        Directory.CreateDirectory(mismatchedFolder);
        await File.WriteAllTextAsync(
            Path.Combine(mismatchedFolder, LocalSyncFilename("mismatched")),
            SwiftStyleSyncPayload("mismatched", "claude"));
        await File.WriteAllTextAsync(
            Path.Combine(folder, "manifest.json"),
            """
            {"schema_version":2,"conversations":[
              {"agent":"codex","id":"outside","file":"../outside.json"}
            ]}
            """);

        var result = await new LocalFolderSyncService(repository).SyncAsync(folder);
        Assert.Equal(0, result.Downloaded);
        Assert.Equal(0, await repository.CountAsync());
        Assert.Equal(outsideData, await File.ReadAllBytesAsync(outside));
    }

    [Fact]
    public async Task UpdateServiceAcceptsOnlyAIMemoryWindowsAssets()
    {
        var json = """
            {
              "tag_name":"v1.2.0",
              "name":"AI Memory 1.2",
              "body":"notes",
              "html_url":"https://example.test/releases/1.2",
              "assets":[
                {"name":"ChatMem.msixbundle","browser_download_url":"https://example.test/chatmem.msixbundle"},
                {"name":"AIMemory-x64.msix","browser_download_url":"https://example.test/aimemory.msix"},
                {"name":"AI-Memory.msixbundle","browser_download_url":"https://example.test/aimemory.msixbundle"}
              ]
            }
            """;
        var handler = new StaticHttpHandler(json);
        var result = await new UpdateService(new HttpClient(handler))
            .CheckAsync("https://example.test/latest", "1.1.9");
        Assert.True(result.IsUpdateAvailable);
        Assert.Equal("1.2.0", result.Release.Version);
        Assert.Equal("AI-Memory.msixbundle", result.Release.AssetName);
        Assert.Equal(0, UpdateService.CompareVersions("1.2", "1.2.0"));
        Assert.True(UpdateService.CompareVersions("2.0", "1.99") > 0);
    }

    [Fact]
    public void ConversationProjectionFiltersProjectsSearchesAndSorts()
    {
        var conversations = new[]
        {
            new ConversationSummary(
                "a", "alpha", "codex", "source-a", "Zebra",
                DateTimeOffset.Parse("2026-07-01T00:00:00Z"),
                DateTimeOffset.Parse("2026-07-04T00:00:00Z"),
                null, @"C:\repos\Alpha"),
            new ConversationSummary(
                "b", "beta", "claude", "source-b", "Alpha",
                DateTimeOffset.Parse("2026-07-04T00:00:00Z"),
                DateTimeOffset.Parse("2026-07-01T00:00:00Z"),
                null, @"C:\repos\Beta"),
            new ConversationSummary(
                "c", "alpha", "gemini", "source-c", "Bravo",
                DateTimeOffset.Parse("2026-07-02T00:00:00Z"),
                DateTimeOffset.Parse("2026-07-03T00:00:00Z"),
                null, @"c:\repos\alpha\"),
            new ConversationSummary(
                "d", "fallback", "opencode", "source-d", "Query match",
                DateTimeOffset.Parse("2026-07-03T00:00:00Z"),
                DateTimeOffset.Parse("2026-07-02T00:00:00Z"),
                null),
        };

        var projects =
            ConversationListProjectionService.Projects(conversations);
        Assert.Equal(3, projects.Count);
        Assert.Contains(projects, value =>
            value.Label.Equals("Alpha", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(projects, value => value.Label == "fallback");
        var groups =
            ConversationListProjectionService.GroupByProject(conversations);
        Assert.Equal(3, groups.Count);
        Assert.Equal("Alpha", groups[0].Label,
            ignoreCase: true);
        Assert.Equal(["a", "c"],
            groups[0].Conversations.Select(value => value.Id));

        var alpha = new HashSet<string>(
            [@"C:\REPOS\ALPHA"],
            StringComparer.OrdinalIgnoreCase);
        Assert.Equal(
            ["a", "c"],
            ConversationListProjectionService.Apply(
                    conversations,
                    null,
                    null,
                    alpha,
                    ConversationSortMode.UpdatedDescending)
                .Select(value => value.Id));
        Assert.Equal(
            ["c", "a"],
            ConversationListProjectionService.Apply(
                    conversations,
                    null,
                    null,
                    alpha,
                    ConversationSortMode.CreatedDescending)
                .Select(value => value.Id));
        Assert.Equal(
            ["c", "a"],
            ConversationListProjectionService.Apply(
                    conversations,
                    null,
                    null,
                    alpha,
                    ConversationSortMode.TitleAscending)
                .Select(value => value.Id));
        Assert.Equal(
            "b",
            Assert.Single(ConversationListProjectionService.Apply(
                conversations,
                null,
                "beta",
                null,
                ConversationSortMode.UpdatedDescending)).Id);
        Assert.Equal(
            "d",
            Assert.Single(ConversationListProjectionService.Apply(
                conversations,
                null,
                "query",
                null,
                ConversationSortMode.UpdatedDescending)).Id);
        Assert.Equal(
            "b",
            Assert.Single(ConversationListProjectionService.Apply(
                conversations,
                "claude",
                null,
                null,
                ConversationSortMode.UpdatedDescending)).Id);
    }

    [Fact]
    public async Task DiagnosticsReadsActualDatabaseAndAgentState()
    {
        var databasePath = Path.Combine(_root, "diagnostics.db");
        var database = new AIMemoryDatabase(databasePath);
        await database.InitializeAsync();
        await using (var connection = database.OpenConnection())
        {
            var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO conversations VALUES(
                  'diag-1','repo','codex','source','title',$now,$now,NULL);
                INSERT INTO messages VALUES('diag-m1','diag-1','user','hello',$now);
                """;
            insert.Parameters.AddWithValue(
                "$now", DateTimeOffset.UtcNow.ToString("O"));
            await insert.ExecuteNonQueryAsync();
        }
        var bin = Path.Combine(_root, "diag-bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "codex.exe"), "");
        var report = await new DiagnosticsService(
            database,
            new AgentCatalog(_root, [bin])).CollectAsync("0.1.0");
        Assert.Equal(1, report.SchemaVersion);
        Assert.Equal(1, report.Conversations);
        Assert.Equal(1, report.Messages);
        Assert.Equal(1, report.DetectedAgents);
        Assert.Equal(165, report.CatalogAgents);
        Assert.Contains(databasePath, report.ToDisplayText());
    }

    private static WebDavConversationDetail LocalFolderSemanticFixture() =>
        new(
            "vector-1",
            "codex",
            "/tmp/semantic",
            "2026-07-23T10:00:00Z",
            "2026-07-23T11:00:00Z",
            "跨平台",
            null,
            "ignored resume",
            [
                new WebDavMessage(
                    "m-1",
                    "2026-07-23T10:30:00Z",
                    "user",
                    "hello 🌿",
                    [
                        new WebDavToolCall(
                            "tool-1",
                            "shell",
                            JsonSerializer.SerializeToElement(new
                            {
                                z = new
                                {
                                    nested = new object?[] { true, null, "x" },
                                },
                                alpha = 1,
                            }),
                            null,
                            "completed"),
                    ],
                    new Dictionary<string, JsonElement>
                    {
                        ["ignored"] = JsonSerializer.SerializeToElement("metadata"),
                    }),
            ],
            [
                new WebDavFileChange(
                    "/tmp/a.swift",
                    "modified",
                    "2026-07-23T10:31:00Z",
                    "m-1"),
            ]);

    private static string LocalSyncFilename(string id) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(id))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".json";

    private static string SwiftStyleSyncPayload(
        string id,
        string agent = "codex") =>
        $$"""
        {
          "id": "{{id}}",
          "source_agent": "{{agent}}",
          "project_dir": "/tmp/sync-project",
          "created_at": "2026-07-23T10:00:00Z",
          "updated_at": "2026-07-23T11:00:00Z",
          "summary": "codex conversation",
          "messages": [
            {
              "id": "{{id}}-message",
              "timestamp": "2026-07-23T11:00:00Z",
              "role": "user",
              "content": "hello",
              "tool_calls": [
                {
                  "id": "{{id}}-tool",
                  "name": "read_file",
                  "input": {"path":"README.md","answer":42},
                  "output": null,
                  "status": "success"
                }
              ]
            }
          ],
          "file_changes": []
        }
        """;

    private static async Task InsertRestoreConversationAsync(
        AIMemoryDatabase database,
        string id,
        string title)
    {
        await using var connection = database.OpenConnection();
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO conversations VALUES(
              $id,'repo','codex','source',$title,$now,$now,NULL);
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue(
            "$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task CreateOpenCodeMigrationStoreAsync(string home)
    {
        var directory = Path.Combine(
            home, ".local", "share", "opencode");
        Directory.CreateDirectory(directory);
        await using var connection = new SqliteConnection(
            $"Data Source={Path.Combine(directory, "opencode.db")}");
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE project(
              id TEXT PRIMARY KEY,worktree TEXT,vcs TEXT,name TEXT,
              time_created INTEGER,time_updated INTEGER,sandboxes TEXT);
            CREATE TABLE session(
              id TEXT PRIMARY KEY,project_id TEXT,slug TEXT,directory TEXT,
              title TEXT,version TEXT,summary_files INTEGER,
              time_created INTEGER,time_updated INTEGER,time_archived INTEGER);
            CREATE TABLE message(
              id TEXT PRIMARY KEY,session_id TEXT,time_created INTEGER,
              time_updated INTEGER,data TEXT);
            CREATE TABLE part(
              id TEXT PRIMARY KEY,message_id TEXT,session_id TEXT,
              time_created INTEGER,time_updated INTEGER,data TEXT);
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertDatabaseBackedSourceStateAsync(
        string home,
        string source,
        NativeAgentWriteResult written,
        string expected)
    {
        var databasePath = source == "codex"
            ? Path.Combine(home, ".codex", "state_5.sqlite")
            : Path.Combine(
                home, ".local", "share", "opencode", "opencode.db");
        await using var connection = new SqliteConnection(
            $"Data Source={databasePath}");
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = source == "codex"
            ? "SELECT COUNT(*) FROM threads WHERE id=$id;"
            : "SELECT time_archived FROM session WHERE id=$id;";
        command.Parameters.AddWithValue("$id", written.Id);
        var value = await command.ExecuteScalarAsync();

        if (source == "codex")
        {
            var count = Convert.ToInt32(value);
            Assert.Equal(expected == "active" ? 1 : 0, count);
            Assert.Equal(expected == "active", File.Exists(written.StoragePath));
            return;
        }
        switch (expected)
        {
            case "active":
                Assert.Equal(DBNull.Value, value);
                break;
            case "archived":
                Assert.NotNull(value);
                Assert.NotEqual(DBNull.Value, value);
                break;
            case "missing":
                Assert.Null(value);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(expected));
        }
    }

    public void Dispose()
    {
        // Microsoft.Data.Sqlite pools native handles by default. Linux allows
        // deleting an open SQLite file, while Windows correctly keeps it
        // locked until the pool is cleared, so release idle test connections
        // before removing the isolated fixture directory.
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class MemoryWebDavHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _files = [];
        private readonly HashSet<string> _collections = [];

        public int ConversationPutCount { get; private set; }
        public int ManifestPutCount { get; private set; }

        public void Seed(string path, byte[] data) => _files[path] = data;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method.Method == "PROPFIND")
            {
                return new HttpResponseMessage(
                    _collections.Contains(path)
                        ? (System.Net.HttpStatusCode)207
                        : System.Net.HttpStatusCode.NotFound);
            }
            if (request.Method.Method == "MKCOL")
            {
                _collections.Add(path);
                return new HttpResponseMessage(System.Net.HttpStatusCode.Created);
            }
            if (request.Method == HttpMethod.Put)
            {
                _files[path] = await request.Content!.ReadAsByteArrayAsync(
                    cancellationToken);
                if (path.EndsWith("/manifest.json", StringComparison.Ordinal))
                {
                    ManifestPutCount++;
                }
                else
                {
                    ConversationPutCount++;
                }
                return new HttpResponseMessage(System.Net.HttpStatusCode.Created);
            }
            if (request.Method == HttpMethod.Get)
            {
                if (!_files.TryGetValue(path, out var data))
                {
                    return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
                }
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(data),
                };
            }
            return new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest);
        }
    }

    private sealed class StaticHttpHandler(string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json"),
            });
    }
}
