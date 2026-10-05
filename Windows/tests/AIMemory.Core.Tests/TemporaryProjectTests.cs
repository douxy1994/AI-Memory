// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
using AIMemory.Core.Models;
using AIMemory.Core.Persistence;
using AIMemory.Core.Services;
using System.Text.Json;
using Xunit;

namespace AIMemory.Core.Tests;

public sealed class TemporaryProjectTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AIMemoryFilteringTests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("/tmp", true)]
    [InlineData("/tmp/work", true)]
    [InlineData("/private/tmp/work/", true)]
    [InlineData("/var/tmp/work", true)]
    [InlineData("/private/var/tmp/work", true)]
    [InlineData("/var/folders/ab/session", true)]
    [InlineData("/private/var/folders/ab/session", true)]
    [InlineData("/private/var/../tmp/work", true)]
    [InlineData("/tmp/../home/project", false)]
    [InlineData("/tmp-project", false)]
    [InlineData("/TMP/project", false)]
    [InlineData("/home/me/repo/tmp", false)]
    [InlineData("/home/me/temperature-cache", false)]
    [InlineData(@"c:\USERS\TEST\APPDATA\LOCAL\TEMP\work\", true)]
    [InlineData("C:/Users/Test/AppData/Local/Temp/./work", true)]
    [InlineData(@"\\?\C:\Users\Test\AppData\Local\Temp\work", true)]
    [InlineData(@"C:\Users\Test\AppData\Local\TempProject", false)]
    [InlineData(@"D:\Users\Test\AppData\Local\Temp\work", false)]
    [InlineData(@"C:\Users\Test\AppData\Local\Temp\..\Projects", false)]
    [InlineData(@"\\SERVER\Share\Temp\work", true)]
    [InlineData(@"\\?\UNC\server\share\Temp\work", true)]
    [InlineData(@"\\server\share\TempProject", false)]
    [InlineData(@"\\server\other\Temp\work", false)]
    [InlineData(@"C:Temp\work", false)]
    [InlineData("temp/work", false)]
    [InlineData("", false)]
    public void NormalizesOnlyWithinTheCorrectPlatformAndVolume(string path, bool expected) =>
        Assert.Equal(expected, TemporaryProjectPolicy.IsTemporaryProject(path,
            [@"C:\Users\Test\AppData\Local\Temp\", @"\\server\share\Temp"]));

    [Fact]
    public void DetectsActualLocalTemporaryRoots()
    {
        Assert.True(TemporaryProjectPolicy.IsTemporaryProject(_root));
        foreach (var name in new[] { "TEMP", "TMP" })
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } root)
                Assert.True(TemporaryProjectPolicy.IsTemporaryProject(Path.Combine(root, "project")));
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrEmpty(windows))
            Assert.True(TemporaryProjectPolicy.IsTemporaryProject(Path.Combine(windows, "Temp", "project")));
    }

    [Fact]
    public async Task ExistingRowsAreHiddenBeforeLimitButExplicitReadsAndFolderSyncRetainThem()
    {
        var database = new AIMemoryDatabase(Path.Combine(_root, "source.db"));
        await database.InitializeAsync();
        var repository = new ConversationRepository(database);
        await repository.UpsertAsync(Detail("normal", @"C:\Projects\AIMemory\tmp", "2026-10-01T00:00:00Z"));
        await repository.UpsertAsync(Detail("temporary", _root, "2026-10-02T00:00:00Z"));
        await repository.UpsertAsync(Detail("posix", "/private/var/folders/session", "2026-10-03T00:00:00Z"));
        Assert.Equal("normal", Assert.Single(await repository.ListAsync(limit: 1)).Id);
        Assert.Empty(await repository.ListAsync(search: "temporary"));
        Assert.Equal(3, await repository.CountAsync());
        Assert.NotNull(await repository.FindAsync("temporary"));
        Assert.Equal(_root, (await repository.ExportAsync("temporary")).ProjectDir);
        Assert.Single(await repository.ReadMessagesAsync("temporary"));
        var all = await repository.ListAsync(includeTemporary: true);
        Assert.Equal(3, all.Count);
        Assert.Single(ConversationListProjectionService.Projects(all));
        Assert.Single(ConversationListProjectionService.GroupByProject(all));
        Assert.Single(ConversationListProjectionService.Apply(all, null, null, null, ConversationSortMode.UpdatedDescending));
        Assert.Empty(await new MemoryQueryService(database).SearchAsync(_root, "question", 10));
        Assert.Empty((await new MemoryQueryService(database).GetProjectContextAsync(_root, "", 3)).RelevantHistory);
        Assert.Empty((await new McpProjectContextService(database).GetProjectContextAsync(_root, "question", "test", 10)).RelevantHistory);
        Assert.Single(await new MemoryQueryService(database).SearchAsync(@"C:\Projects\AIMemory\tmp", "question", 10));

        var folder = Path.Combine(_root, "sync");
        await new LocalFolderSyncService(repository).SyncAsync(folder);
        var restoredDatabase = new AIMemoryDatabase(Path.Combine(_root, "restored.db"));
        await restoredDatabase.InitializeAsync();
        var restored = new ConversationRepository(restoredDatabase);
        await new LocalFolderSyncService(restored).SyncAsync(folder);
        Assert.Equal(3, await restored.CountAsync());
        Assert.Single(await restored.ListAsync());
        Assert.Equal(_root, (await restored.ExportAsync("temporary")).ProjectDir);
    }

    [Fact]
    public async Task NativeImportCountsOnlyNonTemporaryProjectsAndKeepsGeminiStorageReadOnly()
    {
        var database = new AIMemoryDatabase(Path.Combine(_root, "import.db"));
        await database.InitializeAsync();
        var repository = new ConversationRepository(database);
        var chats = Path.Combine(_root, "home", ".gemini", "tmp", "hash", "chats");
        Directory.CreateDirectory(chats);
        foreach (var (id, project) in new[] { ("normal", @"C:\Projects\AIMemory\tmp"), ("temporary", _root), ("posix", "/tmp/import") })
        {
            await File.WriteAllTextAsync(Path.Combine(chats, id + ".json"), JsonSerializer.Serialize(new
            {
                sessionId = id, projectPath = project,
                startTime = "2026-10-01T00:00:00Z", lastUpdated = "2026-10-01T00:01:00Z",
                messages = new[] { new { id = id + "-message", type = "user", content = "Import question", timestamp = "2026-10-01T00:00:00Z" } }
            }));
        }
        var before = Directory.GetFiles(chats).ToDictionary(path => path, File.ReadAllBytes);
        var importer = new NativeHistoryImportService(repository, Path.Combine(_root, "home"));
        Assert.Equal(1, await importer.ImportAgentAsync("gemini"));
        Assert.Equal("normal", Assert.Single(await repository.ListAsync()).Id);
        Assert.Equal(1, await repository.CountAsync());
        Assert.Equal(1, await importer.ImportAgentAsync("gemini"));
        Assert.Equal(1, await repository.CountAsync());
        foreach (var (path, bytes) in before) Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
    }

    private static WebDavConversationDetail Detail(string id, string project, string timestamp) =>
        new(id, "codex", project, timestamp, timestamp, id, "/home/me/.gemini/tmp/session.json", null,
            [new(id + "-message", timestamp, "user", "question", [], [])], []);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
