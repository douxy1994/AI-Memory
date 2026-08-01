// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
using AIMemory.Core.Persistence;

namespace AIMemory.Core.Services;

public sealed record DiagnosticsReport(
    string AppVersion,
    string OperatingSystem,
    string Architecture,
    string DatabasePath,
    long DatabaseBytes,
    int SchemaVersion,
    int Conversations,
    int Messages,
    int ApprovedMemories,
    int PendingCandidates,
    int Checkpoints,
    int Handoffs,
    int DetectedAgents,
    int CatalogAgents)
{
    public string ToDisplayText() =>
        $"""
        AI Memory {AppVersion}
        系统：{OperatingSystem}
        架构：{Architecture}
        数据库：{DatabasePath}
        数据库大小：{DatabaseBytes:N0} bytes
        架构版本：{SchemaVersion}
        对话：{Conversations}
        消息：{Messages}
        已批准记忆：{ApprovedMemories}
        待复核候选：{PendingCandidates}
        检查点：{Checkpoints}
        交接包：{Handoffs}
        已检测 Agent / CLI：{DetectedAgents}/{CatalogAgents}
        """;
}

public sealed class DiagnosticsService(
    AIMemoryDatabase database,
    AgentCatalog? catalog = null)
{
    private readonly AgentCatalog _catalog = catalog ?? new AgentCatalog();

    public async Task<DiagnosticsReport> CollectAsync(
        string appVersion,
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.OpenConnection();
        var values = new Dictionary<string, int>();
        foreach (var (name, sql) in new[]
        {
            ("schema", "PRAGMA user_version;"),
            ("conversations", "SELECT COUNT(*) FROM conversations;"),
            ("messages", "SELECT COUNT(*) FROM messages;"),
            ("memories", "SELECT COUNT(*) FROM approved_memories WHERE status='approved';"),
            ("candidates", "SELECT COUNT(*) FROM memory_candidates WHERE status='pending_review';"),
            ("checkpoints", "SELECT COUNT(*) FROM checkpoints;"),
            ("handoffs", "SELECT COUNT(*) FROM handoff_packets;"),
        })
        {
            var command = connection.CreateCommand();
            command.CommandText = sql;
            values[name] = Convert.ToInt32(
                await command.ExecuteScalarAsync(cancellationToken));
        }
        var agents = _catalog.Detect();
        return new DiagnosticsReport(
            appVersion,
            Environment.OSVersion.VersionString,
            System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
            database.Path,
            File.Exists(database.Path) ? new FileInfo(database.Path).Length : 0,
            values["schema"],
            values["conversations"],
            values["messages"],
            values["memories"],
            values["candidates"],
            values["checkpoints"],
            values["handoffs"],
            agents.Count(value => value.IsDetected),
            agents.Count);
    }
}
