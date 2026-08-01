using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIMemory.Core.Models;

public sealed record WebDavConversationDetail(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("source_agent")] string SourceAgent,
    [property: JsonPropertyName("project_dir")] string ProjectDir,
    [property: JsonPropertyName("created_at")] string CreatedAt,
    [property: JsonPropertyName("updated_at")] string UpdatedAt,
    [property: JsonPropertyName("summary")] string? Summary,
    [property: JsonPropertyName("storage_path")] string? StoragePath,
    [property: JsonPropertyName("resume_command")] string? ResumeCommand,
    [property: JsonPropertyName("messages")] IReadOnlyList<WebDavMessage> Messages,
    [property: JsonPropertyName("file_changes")] IReadOnlyList<WebDavFileChange> FileChanges);

public sealed record WebDavMessage(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("timestamp")] string Timestamp,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("tool_calls")] IReadOnlyList<WebDavToolCall> ToolCalls,
    [property: JsonPropertyName("metadata")] Dictionary<string, JsonElement> Metadata);

public sealed record WebDavToolCall(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("input")] JsonElement Input,
    [property: JsonPropertyName("output")] string? Output,
    [property: JsonPropertyName("status")] string Status);

public sealed record WebDavFileChange(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("change_type")] string ChangeType,
    [property: JsonPropertyName("timestamp")] string Timestamp,
    [property: JsonPropertyName("message_id")] string? MessageId);

/// <summary>
/// A source-backed conversation detail plus the MCP-specific message window.
/// The full detail remains available so callers retain tool calls, metadata,
/// file changes, storage information, and resume command when the message list
/// is narrowed around a requested message or query.
/// </summary>
public sealed record McpConversationReadResult(
    WebDavConversationDetail Detail,
    IReadOnlyList<WebDavMessage> Messages,
    int ReturnedMessageCount,
    string FocusedMessageId);

public sealed record WebDavManifest(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("generated_at")] string GeneratedAt,
    [property: JsonPropertyName("conversations")] IReadOnlyList<WebDavManifestEntry> Conversations);

public sealed record WebDavManifestEntry(
    [property: JsonPropertyName("agent")] string Agent,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("updated_at")] string UpdatedAt,
    [property: JsonPropertyName("sha256")] string? Sha256,
    [property: JsonPropertyName("semantic_digest")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? SemanticDigest = null);
