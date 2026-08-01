// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
using AIMemory.Core.Models;
using AIMemory.Core.Persistence;

namespace AIMemory.Core.Services;

public sealed record ConversationMigrationResult(
    string NewId,
    string Source,
    string Target,
    string Mode,
    bool Verified,
    int SourceMessageCount,
    int TargetMessageCount,
    int SourceToolCallCount,
    int TargetToolCallCount,
    int SourceFileCount,
    int TargetFileCount,
    bool FirstUserPreserved,
    bool CutDeletedSource,
    IReadOnlyList<string> Warnings);

/// Migrates a conversation into a real target Agent store, re-imports that
/// store, and accepts success only after content verification.
public sealed class ConversationMigrationService
{
    private readonly ConversationRepository _repository;
    private readonly NativeAgentConversationWriter _writer;
    private readonly NativeHistoryImportService _importer;

    public ConversationMigrationService(
        ConversationRepository repository,
        string? home = null,
        NativeAgentConversationWriter? writer = null,
        NativeHistoryImportService? importer = null)
    {
        _repository = repository;
        _writer = writer ?? new NativeAgentConversationWriter(home);
        _importer = importer
            ?? new NativeHistoryImportService(repository, home);
    }

    public async Task<ConversationMigrationResult> CopyAsync(
        string source,
        string target,
        string conversationId,
        CancellationToken cancellationToken = default)
        => await MigrateAsync(
            source,
            target,
            conversationId,
            "copy",
            null,
            14,
            cancellationToken);

    public async Task<ConversationMigrationResult> MigrateAsync(
        string source,
        string target,
        string conversationId,
        string mode,
        TrashService? trash,
        int retentionDays = 14,
        CancellationToken cancellationToken = default)
    {
        if (mode is not ("copy" or "cut"))
        {
            throw new ArgumentOutOfRangeException(
                nameof(mode), "迁移方式只能是 copy 或 cut。");
        }
        if (mode == "cut" && trash is null)
        {
            throw new ArgumentNullException(
                nameof(trash), "移动迁移需要可恢复回收站服务。");
        }
        if (mode == "cut"
            && !NativeAgentConversationWriter.ArchivableSources.Contains(source))
        {
            throw new NotSupportedException(
                $"源 Agent {source} 不支持安全移动；请选择复制。");
        }
        if (source.Equals(target, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("源 Agent 与目标 Agent 不能相同。");
        }
        if (!NativeAgentConversationWriter.WritableTargets.Contains(target))
        {
            throw new NotSupportedException(
                $"目标 Agent {target} 不支持安全的原生会话写入。");
        }

        var summary = await _repository.FindAsync(
                conversationId, cancellationToken)
            ?? throw new KeyNotFoundException($"找不到对话 {conversationId}。");
        var original = await _repository.ExportAsync(
            conversationId, cancellationToken);
        if (!original.SourceAgent.Equals(
                source, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"对话属于 {original.SourceAgent}，不是请求的源 Agent {source}。");
        }
        NativeAgentWriteResult? written = null;
        var verifiedWrite = false;
        try
        {
            written = await _writer.WriteAsync(
                original, target, cancellationToken);
            var report = await _importer.ImportAllAsync(cancellationToken);
            WebDavConversationDetail verified;
            try
            {
                verified = await _repository.ExportAsync(
                    written.Id, cancellationToken);
            }
            catch
            {
                await _writer.DiscardAsync(
                    written, target, cancellationToken);
                await _repository.DeleteAsync(written.Id, cancellationToken);
                throw new InvalidDataException(
                    report.Warnings.Count == 0
                        ? "目标会话无法回读，已撤销目标写入。"
                        : $"目标会话无法回读，已撤销目标写入：{string.Join("；", report.Warnings)}");
            }

            var sourceFirst = FirstUser(original);
            var targetFirst = FirstUser(verified);
            var firstPreserved = string.Equals(
                sourceFirst, targetFirst, StringComparison.Ordinal);
            var sourceTools = ToolSignatures(original);
            var targetTools = ToolSignatures(verified);
            var toolsPreserved = sourceTools.SequenceEqual(
                targetTools, StringComparer.Ordinal);
            var sourceFiles = FilePaths(original);
            var targetFiles = FilePaths(verified);
            var filesPreserved = sourceFiles.SetEquals(targetFiles);
            if (original.Messages.Count != verified.Messages.Count
                || !firstPreserved
                || !toolsPreserved
                || !filesPreserved)
            {
                await _writer.DiscardAsync(
                    written, target, cancellationToken);
                await _repository.DeleteAsync(written.Id, cancellationToken);
                throw new InvalidDataException(
                    $"目标内容验证失败（消息 {original.Messages.Count}→"
                    + $"{verified.Messages.Count}，首条用户消息"
                    + $"{(firstPreserved ? "一致" : "不一致")}，工具调用 "
                    + $"{sourceTools.Count}→{targetTools.Count}，文件 "
                    + $"{sourceFiles.Count}→{targetFiles.Count}），"
                    + "已撤销目标写入。");
            }
            verifiedWrite = true;

            var cutDeletedSource = false;
            if (mode == "cut")
            {
                var archive = await _writer.ArchiveSourceAsync(
                    original, cancellationToken);
                try
                {
                    await trash!.TrashAsync(
                        summary,
                        retentionDays,
                        sourceArchive: archive,
                        detailOverride: original,
                        cancellationToken: cancellationToken);
                    cutDeletedSource = true;
                }
                catch
                {
                    await _writer.RestoreSourceArchiveAsync(
                        archive, cancellationToken);
                    throw;
                }
            }

            return new ConversationMigrationResult(
                written.Id,
                source,
                target,
                mode,
                true,
                original.Messages.Count,
                verified.Messages.Count,
                sourceTools.Count,
                targetTools.Count,
                sourceFiles.Count,
                targetFiles.Count,
                firstPreserved,
                cutDeletedSource,
                report.Warnings);
        }
        catch
        {
            if (written is not null && !verifiedWrite)
            {
                try
                {
                    await _writer.DiscardAsync(
                        written, target, cancellationToken);
                    await _repository.DeleteAsync(
                        written.Id, cancellationToken);
                }
                catch
                {
                    // Keep the original migration exception. The target path is
                    // included in its preceding write/verification context.
                }
            }
            throw;
        }
    }

    public static string ContinuationCard(
        WebDavConversationDetail conversation)
    {
        var latest = conversation.Messages
            .Where(message => !string.IsNullOrWhiteSpace(message.Content))
            .TakeLast(6)
            .Select(message =>
                $"- {message.Role}: {message.Content.Trim()}")
            .ToArray();
        return string.Join(
            Environment.NewLine,
            new[]
            {
                "# AI Memory Continuation Card",
                "",
                $"Source: {conversation.SourceAgent}/{conversation.Id}",
                $"Project: {conversation.ProjectDir}",
                $"Updated: {conversation.UpdatedAt}",
                $"Summary: {conversation.Summary ?? "未命名对话"}",
                "",
                "## Recent Context",
                latest.Length == 0
                    ? "- No retained messages."
                    : string.Join(Environment.NewLine, latest),
                "",
                "Use AI Memory history search to expand the source conversation when needed.",
            });
    }

    private static string? FirstUser(WebDavConversationDetail conversation) =>
        conversation.Messages.FirstOrDefault(message =>
            message.Role.Equals("user", StringComparison.OrdinalIgnoreCase))
            ?.Content;

    private static IReadOnlyList<string> ToolSignatures(
        WebDavConversationDetail conversation) =>
        conversation.Messages
            .SelectMany(message => message.ToolCalls)
            .Select(tool =>
                string.Join(
                    "\u001f",
                    tool.Name,
                    tool.Status.ToLowerInvariant(),
                    tool.Output ?? ""))
            .ToArray();

    private static HashSet<string> FilePaths(
        WebDavConversationDetail conversation) =>
        conversation.FileChanges
            .Select(change => change.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
