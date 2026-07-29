using AIMemory.Core.Models;
using AIMemory.Core.Persistence;
using AIMemory.Core.Services;
using Microsoft.Data.Sqlite;
using System.Net;
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
            Sync = new SyncSettings { WebdavHost = "dav.example.com" },
        };
        await store.SaveAsync(settings);
        var loaded = await store.LoadAsync();
        Assert.Equal(365, loaded.TrashRetentionDays);
        Assert.Equal("dav.example.com", loaded.Sync.WebdavHost);
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
        ], AgentCatalog.All.Select(value => value.Id).ToArray());
        var firstMissing = statuses
            .Select((status, index) => (status, index))
            .First(value => !value.status.IsDetected).index;
        Assert.Equal(2, firstMissing);
        Assert.Equal(["goose", "vibe"], statuses
            .Take(firstMissing).Select(value => value.Id).ToArray());
        Assert.All(statuses.Take(firstMissing), value => Assert.True(value.IsDetected));
        Assert.All(statuses.Skip(firstMissing), value =>
        {
            Assert.False(value.IsDetected);
            Assert.False(value.IsIntegrated);
            Assert.Equal(AgentIntegrationState.Missing, value.State);
        });
        Assert.Equal(45, statuses.Count);
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
                """;
            insert.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            await insert.ExecuteNonQueryAsync();
        }
        var service = new MemoryGovernanceService(database);
        Assert.Single(await service.ListCandidatesAsync());
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
        var second = await service.SyncAsync(folder);
        Assert.Equal(0, second.Uploaded);
        Assert.Equal(1, second.Skipped);
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
        Assert.Equal(45, report.CatalogAgents);
        Assert.Contains(databasePath, report.ToDisplayText());
    }

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
