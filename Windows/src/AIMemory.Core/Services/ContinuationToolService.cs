// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
using AIMemory.Core.Models;
using AIMemory.Core.Persistence;

namespace AIMemory.Core.Services;

public sealed class ContinuationToolService(
    AIMemoryDatabase database,
    ConversationRepository conversations,
    RepositoryGovernanceService governance)
{
    public async Task<CheckpointRecord> CreateCheckpointAsync(
        string repoRoot,
        string conversationId,
        string sourceAgent,
        string summary,
        string? resumeCommand,
        string? metadataJson,
        CancellationToken cancellationToken = default)
    {
        var repoId = await governance.ResolveRepoIdAsync(
            repoRoot,
            cancellationToken: cancellationToken)
            ?? throw new KeyNotFoundException("找不到仓库。");
        var conversation = await conversations.FindAsync(
                conversationId,
                cancellationToken)
            ?? throw new KeyNotFoundException(
                $"找不到来源对话 {conversationId}。");
        if (!conversation.SourceAgent.Equals(
                sourceAgent,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "来源 Agent 与对话记录不一致。");
        }
        if (conversation.RepoId != repoId)
        {
            throw new InvalidOperationException(
                "来源对话不属于指定仓库。");
        }
        var messageCount = (await conversations.ReadMessagesAsync(
            conversationId,
            cancellationToken)).Count;
        return await new RecoveryService(database).CreateCheckpointAsync(
            conversation,
            messageCount,
            summary,
            resumeCommand,
            metadataJson,
            cancellationToken);
    }

    public async Task<HandoffRecord> BuildHandoffAsync(
        string repoRoot,
        string fromAgent,
        string toAgent,
        string? goalHint,
        string? targetProfile,
        CancellationToken cancellationToken = default)
    {
        var repoId = await governance.ResolveRepoIdAsync(
            repoRoot,
            cancellationToken: cancellationToken)
            ?? throw new KeyNotFoundException("找不到仓库。");
        var recovery = new RecoveryService(database);
        var checkpoint = (await recovery.ListCheckpointsAsync(cancellationToken))
            .FirstOrDefault(value =>
                value.RepoId == repoId
                && value.SourceAgent.Equals(
                    fromAgent,
                    StringComparison.OrdinalIgnoreCase)
                && value.Status == "active")
            ?? throw new KeyNotFoundException(
                "当前仓库没有可用于交接的活动检查点。");
        if (!string.IsNullOrWhiteSpace(goalHint))
        {
            checkpoint = checkpoint with { Summary = goalHint.Trim() };
        }
        return await recovery.CreateHandoffAsync(
            checkpoint,
            toAgent,
            targetProfile,
            cancellationToken);
    }

    public async Task<HandoffRecord> ResumeFromCheckpointAsync(
        string checkpointId,
        string toAgent,
        string? targetProfile,
        CancellationToken cancellationToken = default)
    {
        var recovery = new RecoveryService(database);
        var checkpoint = (await recovery.ListCheckpointsAsync(cancellationToken))
            .FirstOrDefault(value => value.Id == checkpointId)
            ?? throw new KeyNotFoundException(
                $"找不到检查点 {checkpointId}。");
        return await recovery.CreateHandoffAsync(
            checkpoint,
            toAgent,
            targetProfile,
            cancellationToken);
    }

    public async Task<IReadOnlyList<AgentRunRecord>> ListRunsAsync(
        string repoRoot,
        CancellationToken cancellationToken = default)
    {
        var repoId = await governance.ResolveRepoIdAsync(
            repoRoot,
            cancellationToken: cancellationToken);
        if (repoId is null) return [];
        return (await new HistoryProjectionService(database)
                .ListRunsAsync(cancellationToken))
            .Where(value =>
                value.RepoId == repoId
                && !value.Status.Equals(
                    "completed",
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    public async Task<IReadOnlyList<ArtifactRecord>> ListArtifactsAsync(
        string repoRoot,
        CancellationToken cancellationToken = default)
    {
        var repoId = await governance.ResolveRepoIdAsync(
            repoRoot,
            cancellationToken: cancellationToken);
        if (repoId is null) return [];
        var runIds = (await new HistoryProjectionService(database)
                .ListRunsAsync(cancellationToken))
            .Where(value => value.RepoId == repoId)
            .Select(value => value.Id)
            .ToHashSet(StringComparer.Ordinal);
        return (await new HistoryProjectionService(database)
                .ListArtifactsAsync(cancellationToken))
            .Where(value => runIds.Contains(value.RunId))
            .ToArray();
    }

    public async Task<IReadOnlyList<WikiRecord>> ListWikiAsync(
        string repoRoot,
        CancellationToken cancellationToken = default)
    {
        var repoId = await governance.ResolveRepoIdAsync(
            repoRoot,
            cancellationToken: cancellationToken);
        if (repoId is null) return [];
        return (await new HistoryProjectionService(database)
                .ListWikiAsync(cancellationToken))
            .Where(value => value.RepoId == repoId)
            .ToArray();
    }
}
