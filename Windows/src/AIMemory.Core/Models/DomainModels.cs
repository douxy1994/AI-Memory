namespace AIMemory.Core.Models;

public sealed record ConversationSummary(
    string Id,
    string RepoId,
    string SourceAgent,
    string SourceConversationId,
    string Summary,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    string? StoragePath,
    string ProjectPath = "");

public sealed record ConversationMessage(
    string Id,
    string ConversationId,
    string Role,
    string Content,
    DateTimeOffset Timestamp);

public sealed record FavoriteConversationSnapshot(
    string Id,
    string SourceAgent,
    string Title,
    string ProjectPath,
    DateTimeOffset UpdatedAt,
    bool Pinned = false,
    string Note = "",
    IReadOnlyList<string>? Tags = null);

public sealed record TrashRecord(
    string TrashId,
    string Agent,
    string ConversationId,
    string Title,
    DateTimeOffset TrashedAt,
    DateTimeOffset ExpiresAt,
    string RecordPath);

public enum AgentIntegrationState
{
    Missing,
    Detected,
    Integrated,
    Partial,
}

public sealed record AgentIntegrationStatus(
    string Id,
    string Label,
    bool IsDetected,
    bool IsIntegrationAvailable,
    bool IsIntegrated,
    AgentIntegrationState State,
    string Detail)
{
    public string ActionLabel => IsIntegrated ? "关闭" : "启用";
    public bool CanToggle => IsDetected && IsIntegrationAvailable;
}

public sealed record SyncProgress(
    string Phase,
    int Uploaded,
    int Downloaded,
    int Skipped,
    bool Completed,
    string Message);
