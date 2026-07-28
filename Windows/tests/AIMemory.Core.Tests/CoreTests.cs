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
        Assert.Equal(41, statuses.Count);
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
        Assert.Equal(41, report.CatalogAgents);
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
