using AIMemory.Core.Models;
using AIMemory.Core.Persistence;
using AIMemory.Core.Services;
using Xunit;

namespace AIMemory.Core.Tests;

public sealed class ChatMemWebDavImportServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "AIMemoryChatMemWebDavTests",
        Guid.NewGuid().ToString("N"));

    public ChatMemWebDavImportServiceTests()
        => Directory.CreateDirectory(_root);

    [Fact]
    public async Task ImportsEndpointAndPrefersLegacyCredential()
    {
        var source = CreateSource("""
            {
              "sync": {
                "provider": "webdav",
                "webdavScheme": "https",
                "webdavHost": "dav.example.test",
                "webdavPath": "remote.php/dav/files/alvis",
                "username": "alvis",
                "password": "settings-secret",
                "remotePath": "ai-memory",
                "downloadMode": "as-needed"
              }
            }
            """);
        var originalSource = await File.ReadAllBytesAsync(source);
        var store = CreateTargetStore();
        string? savedUsername = null;
        string? savedPassword = null;

        var result = await CreateService(store, source).ImportAsync(
            _ => null,
            _ => "credential-secret",
            (username, password) =>
            {
                savedUsername = username;
                savedPassword = password;
            });

        Assert.True(result.SettingsImported);
        Assert.True(result.CredentialImported);
        Assert.False(result.NeedsAttention);
        Assert.Equal("alvis", savedUsername);
        Assert.Equal("credential-secret", savedPassword);
        var settings = await store.LoadAsync();
        Assert.Equal("webdav", settings.Sync.Provider);
        Assert.Equal("https", settings.Sync.WebdavScheme);
        Assert.Equal("dav.example.test", settings.Sync.WebdavHost);
        Assert.Equal(
            "remote.php/dav/files/alvis",
            settings.Sync.WebdavPath);
        Assert.Equal("alvis", settings.Sync.Username);
        Assert.Equal("ai-memory", settings.Sync.RemotePath);
        Assert.Equal("as-needed", settings.Sync.DownloadMode);
        Assert.Equal(originalSource, await File.ReadAllBytesAsync(source));
        Assert.DoesNotContain(
            "credential-secret",
            await File.ReadAllTextAsync(TargetPath()));
        Assert.DoesNotContain(
            "settings-secret",
            await File.ReadAllTextAsync(TargetPath()));
    }

    [Fact]
    public async Task UsesSettingsPasswordFallbackWithoutPersistingIt()
    {
        var source = CreateSource("""
            {
              "sync": {
                "webdavHost": "fallback.example.test",
                "username": "fallback-user",
                "password": "fallback-secret"
              }
            }
            """);
        var store = CreateTargetStore();
        string? savedPassword = null;

        var result = await CreateService(store, source).ImportAsync(
            _ => null,
            _ => null,
            (_, password) => savedPassword = password);

        Assert.True(result.CredentialImported);
        Assert.Equal("fallback-secret", savedPassword);
        Assert.DoesNotContain(
            "fallback-secret",
            await File.ReadAllTextAsync(TargetPath()));
    }

    [Fact]
    public async Task MatchingEndpointCanImportOnlyMissingCredential()
    {
        var source = CreateSource("""
            {
              "sync": {
                "webdavHost": "dav.example.test",
                "username": "alvis"
              }
            }
            """);
        var store = CreateTargetStore();
        await store.SaveAsync(new AppSettings
        {
            Sync = new SyncSettings
            {
                Provider = "webdav",
                WebdavHost = "dav.example.test",
                Username = "alvis",
            },
        });
        var saves = 0;

        var result = await CreateService(store, source).ImportAsync(
            _ => null,
            _ => "legacy-secret",
            (_, _) => saves += 1);

        Assert.False(result.SettingsImported);
        Assert.True(result.CredentialImported);
        Assert.Equal(1, saves);
    }

    [Fact]
    public async Task ExistingCredentialMakesRepeatedImportNoOp()
    {
        var source = CreateSource("""
            {
              "sync": {
                "webdavHost": "dav.example.test",
                "username": "alvis",
                "password": "source-secret"
              }
            }
            """);
        var store = CreateTargetStore();
        await store.SaveAsync(new AppSettings
        {
            Sync = new SyncSettings
            {
                Provider = "webdav",
                WebdavHost = "dav.example.test",
                Username = "alvis",
            },
        });
        var legacyReads = 0;
        var saves = 0;

        var result = await CreateService(store, source).ImportAsync(
            _ => "current-secret",
            _ =>
            {
                legacyReads += 1;
                return "legacy-secret";
            },
            (_, _) => saves += 1);

        Assert.False(result.Changed);
        Assert.False(result.NeedsAttention);
        Assert.Equal(0, legacyReads);
        Assert.Equal(0, saves);
    }

    [Fact]
    public async Task DoesNotOverwriteDifferentConfiguredEndpoint()
    {
        var source = CreateSource("""
            {
              "sync": {
                "webdavHost": "chatmem.example.test",
                "username": "alvis",
                "password": "source-secret"
              }
            }
            """);
        var store = CreateTargetStore();
        await store.SaveAsync(new AppSettings
        {
            Sync = new SyncSettings
            {
                Provider = "webdav",
                WebdavHost = "aimemory.example.test",
                Username = "alvis",
            },
        });
        var credentialAccesses = 0;

        var result = await CreateService(store, source).ImportAsync(
            _ =>
            {
                credentialAccesses += 1;
                return null;
            },
            _ =>
            {
                credentialAccesses += 1;
                return null;
            },
            (_, _) => credentialAccesses += 1);

        Assert.False(result.Changed);
        Assert.Equal("different_endpoint_configured", result.SkippedReason);
        Assert.Equal(0, credentialAccesses);
        Assert.Equal(
            "aimemory.example.test",
            (await store.LoadAsync()).Sync.WebdavHost);
    }

    [Fact]
    public async Task ReportsMissingPasswordAndNeverWritesSource()
    {
        var source = CreateSource("""
            {
              "sync": {
                "webdavHost": "dav.example.test",
                "username": "alvis"
              }
            }
            """);
        var original = await File.ReadAllTextAsync(source);
        var store = CreateTargetStore();

        var result = await CreateService(store, source).ImportAsync(
            _ => null,
            _ => null,
            (_, _) => throw new InvalidOperationException("must not save"));

        Assert.True(result.SettingsImported);
        Assert.False(result.CredentialImported);
        Assert.True(result.MissingCredential);
        Assert.Equal(original, await File.ReadAllTextAsync(source));
    }

    [Fact]
    public async Task MalformedSourceLeavesExistingTargetUnchanged()
    {
        var source = CreateSource("{ malformed");
        var store = CreateTargetStore();
        await store.SaveAsync(new AppSettings
        {
            Language = "en",
            Sync = new SyncSettings
            {
                Provider = "webdav",
                WebdavHost = "safe.example.test",
                Username = "safe-user",
            },
        });
        var before = await File.ReadAllBytesAsync(TargetPath());

        await Assert.ThrowsAnyAsync<System.Text.Json.JsonException>(
            () => CreateService(store, source).ImportAsync(
                _ => null,
                _ => null,
                (_, _) => { }));

        Assert.Equal(before, await File.ReadAllBytesAsync(TargetPath()));
    }

    [Fact]
    public async Task ReadsLegacyWebDavUrlAndSnakeCaseFields()
    {
        var source = CreateSource("""
            {
              "sync": {
                "webdavUrl": "http://legacy.example.test/root/path/",
                "webdav_username": "legacy-user",
                "remote_path": "legacy-memory",
                "download_mode": "as-needed"
              }
            }
            """);
        var store = CreateTargetStore();

        var result = await CreateService(store, source).ImportAsync(
            _ => null,
            _ => "legacy-secret",
            (_, _) => { });

        Assert.True(result.Changed);
        var settings = await store.LoadAsync();
        Assert.Equal("http", settings.Sync.WebdavScheme);
        Assert.Equal("legacy.example.test", settings.Sync.WebdavHost);
        Assert.Equal("root/path", settings.Sync.WebdavPath);
        Assert.Equal("legacy-user", settings.Sync.Username);
        Assert.Equal("legacy-memory", settings.Sync.RemotePath);
        Assert.Equal("as-needed", settings.Sync.DownloadMode);
    }

    private string CreateSource(string contents)
    {
        var path = Path.Combine(_root, "ChatMem", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    private SettingsStore CreateTargetStore()
        => new(TargetPath());

    private string TargetPath()
        => Path.Combine(_root, "AIMemory", "settings.json");

    private static ChatMemWebDavImportService CreateService(
        SettingsStore store,
        string source)
        => new(store, [source]);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
