// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
using AIMemory.Core.Persistence;
using AIMemory.Core.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AIMemory.Core.Tests;

public sealed class CandidateApprovalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AIMemoryApprovalTests", Guid.NewGuid().ToString("N"));

    private async Task<AIMemoryDatabase> CreateAsync()
    {
        var database = new AIMemoryDatabase(Path.Combine(_root, "approval.db"));
        await database.InitializeAsync();
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO memory_candidates(candidate_id,repo_id,kind,summary,value,why_it_matters,
              confidence,proposed_by,status,created_at,reviewed_at)
            VALUES('candidate','repo','rule','Title','Value','Why',0.9,'test','pending_review','2026-10-04',NULL);
            """;
        await command.ExecuteNonQueryAsync();
        return database;
    }

    [Fact]
    public async Task ApprovedRetryDoesNotThrowOrCreateAnotherRule()
    {
        var service = new MemoryGovernanceService(await CreateAsync());
        var first = await service.ApproveCandidateAsync("candidate", "Title", "Value", "");
        var retry = await service.ApproveCandidateAsync("candidate", "Changed", "Changed", "");
        Assert.True(first.Created);
        Assert.False(retry.Created);
        Assert.Equal(first.MemoryId, retry.MemoryId);
        Assert.Equal("Title", Assert.Single(await service.ListApprovedAsync()).Title);
    }

    [Fact]
    public async Task IndependentConnectionsConcurrentlyApproveExactlyOnce()
    {
        var database = await CreateAsync();
        using var ready = new CountdownEvent(2);
        using var start = new ManualResetEventSlim();
        Task<CandidateApprovalResult> Submit() => Task.Run(async () =>
        {
            // Distinct service/database instances, each opening its own connection.
            var service = new MemoryGovernanceService(new AIMemoryDatabase(database.Path));
            ready.Signal();
            start.Wait();
            return await service.ApproveCandidateAsync("candidate", "Title", "Value", "");
        });
        var first = Submit();
        var second = Submit();
        Assert.True(ready.Wait(TimeSpan.FromSeconds(10)));
        start.Set();
        var results = await Task.WhenAll(first, second);
        Assert.Single(results, value => value.Created);
        Assert.Single(results, value => !value.Created);
        Assert.Equal(results[0].MemoryId, results[1].MemoryId);
        Assert.Single(await new MemoryGovernanceService(database).ListApprovedAsync());
    }

    [Theory]
    [InlineData("", "Value")]
    [InlineData("Title", " ")]
    public async Task EmptyEditRollsBackAndCanRetry(string title, string value)
    {
        var service = new MemoryGovernanceService(await CreateAsync());
        await Assert.ThrowsAsync<ArgumentException>(() => service.ApproveCandidateAsync("candidate", title, value, ""));
        Assert.Single(await service.ListCandidatesAsync());
        Assert.Empty(await service.ListApprovedAsync());
        Assert.True((await service.ApproveCandidateAsync("candidate", "Title", "Value", "")).Created);
    }

    [Theory]
    [InlineData("reject", "rejected")]
    [InlineData("snooze", "snoozed")]
    public async Task ReviewedCandidateCannotBeApproved(string action, string expected)
    {
        var service = new MemoryGovernanceService(await CreateAsync());
        await service.ReviewCandidateAsync("candidate", action);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApproveCandidateAsync("candidate", "Title", "Value", ""));
        Assert.Equal(expected, Assert.Single(await service.ListCandidatesAsync(true)).Status);
        Assert.Empty(await service.ListApprovedAsync());
    }

    [Fact]
    public async Task MissingCandidateFailsWithoutWrites()
    {
        var service = new MemoryGovernanceService(await CreateAsync());
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.ApproveCandidateAsync("missing", "Title", "Value", ""));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.ReviewCandidateAsync("missing", "reject"));
        Assert.Empty(await service.ListApprovedAsync());
    }

    [Fact]
    public async Task ApprovedCandidateRejectAndEmptyEditHaveExplicitErrors()
    {
        var service = new MemoryGovernanceService(await CreateAsync());
        await service.ApproveCandidateAsync("candidate", "Title", "Value", "");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReviewCandidateAsync("candidate", "reject"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ApproveCandidateAsync("candidate", "", "Value", ""));
        Assert.Single(await service.ListApprovedAsync());
    }

    [Fact]
    public async Task ApprovalWaitsForIndependentWriterAndThenCommits()
    {
        var database = await CreateAsync();
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        var service = new MemoryGovernanceService(new AIMemoryDatabase(database.Path));
        using var started = new ManualResetEventSlim();
        var approval = Task.Run(async () =>
        {
            started.Set();
            return await service.ApproveCandidateAsync("candidate", "Title", "Value", "");
        });
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        await Task.Delay(150);
        Assert.False(approval.IsCompleted);
        transaction.Commit();
        Assert.True((await approval).Created);
        Assert.Single(await service.ListApprovedAsync());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
