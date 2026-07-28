using AIMemory.Core.Models;
using AIMemory.Core.Persistence;
using AIMemory.Core.Services;
using Microsoft.Data.Sqlite;
using System.Net;
using System.Text;
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
    public void AgentCatalogKeepsDetectedEntriesBeforeMissingAndNeverEnablesMissing()
    {
        var bin = Path.Combine(_root, "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "goose.exe"), "");
        var statuses = new AgentCatalog(_root, [bin]).Detect();
        var firstMissing = statuses
            .Select((status, index) => (status, index))
            .First(value => !value.status.IsDetected).index;
        Assert.All(statuses.Take(firstMissing), value => Assert.True(value.IsDetected));
        Assert.All(statuses.Skip(firstMissing), value =>
        {
            Assert.False(value.IsDetected);
            Assert.False(value.IsIntegrated);
            Assert.Equal(AgentIntegrationState.Missing, value.State);
        });
        Assert.Equal(34, statuses.Count);
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
    public async Task NativeHistoryImportCopiesClaudeCodexAndGeminiReadOnly()
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

        var database = new AIMemoryDatabase(Path.Combine(_root, "history.db"));
        await database.InitializeAsync();
        var repository = new ConversationRepository(database);
        var report = await new NativeHistoryImportService(repository, home)
            .ImportAllAsync();
        Assert.Equal(3, report.Total);
        Assert.Empty(report.Warnings);
        var conversations = await repository.ListAsync();
        Assert.Equal(3, conversations.Count);
        Assert.Contains(conversations, value => value.SourceAgent == "claude");
        Assert.Contains(conversations, value => value.SourceAgent == "codex");
        Assert.Contains(conversations, value => value.SourceAgent == "gemini");
        Assert.Equal(
            "Claude question",
            (await repository.ReadMessagesAsync("claude-1")).First().Content);

        Assert.True(File.Exists(claudePath));
        Assert.True(File.Exists(rolloutPath));
        Assert.True(File.Exists(geminiPath));
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
        Assert.Equal(34, report.CatalogAgents);
        Assert.Contains(databasePath, report.ToDisplayText());
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
