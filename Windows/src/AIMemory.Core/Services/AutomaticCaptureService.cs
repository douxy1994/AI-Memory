using System.Text.Json;
using AIMemory.Core.Models;
using AIMemory.Core.Persistence;

namespace AIMemory.Core.Services;

public sealed record AutomaticCaptureResult(
    WebDavConversationDetail Detail,
    CheckpointRecord Checkpoint);

public sealed class AutomaticCaptureService
{
    private readonly AIMemoryDatabase _database;
    private readonly ConversationRepository _conversations;
    private readonly NativeHistoryImportService _history;

    public AutomaticCaptureService(
        AIMemoryDatabase database,
        ConversationRepository? conversations = null,
        string? home = null)
    {
        _database = database;
        _conversations = conversations ?? new ConversationRepository(database);
        _history = new NativeHistoryImportService(_conversations, home);
    }

    public async Task<AutomaticCaptureResult> CaptureAsync(
        string sourceAgent,
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        var normalizedSource = sourceAgent.Trim().ToLowerInvariant();
        await _history.ImportAgentAsync(normalizedSource, cancellationToken);
        var conversation = await _conversations.FindAsync(
                conversationId,
                cancellationToken)
            ?? throw new KeyNotFoundException(
                $"找不到来源对话 {conversationId}。");
        if (!conversation.SourceAgent.Equals(
                normalizedSource,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "来源 Agent 与对话记录不一致。");
        }

        var detail = await _conversations.ExportAsync(
            conversationId,
            cancellationToken);
        var metadata = JsonSerializer.Serialize(new
        {
            capture = "auto",
            captured_at = DateTimeOffset.UtcNow.ToString("O"),
            storage_path = detail.StoragePath ?? "",
            message_count = detail.Messages.Count,
            file_count = detail.FileChanges.Count,
            source_conversation_id = detail.Id,
        });
        var checkpoint = await new RecoveryService(_database)
            .UpsertAutomaticCheckpointAsync(
                conversation,
                $"{normalizedSource}:{conversationId}",
                detail.Summary ?? detail.Id,
                detail.ResumeCommand,
                metadata,
                cancellationToken);
        return new AutomaticCaptureResult(detail, checkpoint);
    }
}
