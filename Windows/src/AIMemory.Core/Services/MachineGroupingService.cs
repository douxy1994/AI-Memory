// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
using AIMemory.Core.Models;

namespace AIMemory.Core.Services;

public sealed record MachineProjectGroup(
    string Path,
    string Label,
    string MachineId,
    string MachineLabel,
    IReadOnlyList<ConversationSummary> Conversations)
{
    public ConversationSummary Latest => Conversations
        .OrderByDescending(value => value.UpdatedAt)
        .First();
    public int Count => Conversations.Count;
}

public sealed record MachineGroupView(
    string Id,
    string Label,
    IReadOnlyList<MachineProjectGroup> Projects)
{
    public int ConversationCount =>
        Projects.Sum(project => project.Count);
    public DateTimeOffset LatestAt => Projects
        .Max(project => project.Latest.UpdatedAt);
}

public sealed class MachineGroupingService
{
    private static readonly IReadOnlyDictionary<string, string> DefaultLabels =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["windows"] = "Windows",
            ["macos"] = "Mac",
            ["linux"] = "Linux",
            ["internal"] = "Internal",
            ["other"] = "Other",
        };

    public IReadOnlyList<MachineGroupView> Build(
        IReadOnlyList<ConversationSummary> conversations,
        AppSettings settings)
    {
        var projects = conversations
            .GroupBy(
                conversation => string.IsNullOrWhiteSpace(
                    conversation.ProjectPath)
                    ? conversation.RepoId
                    : conversation.ProjectPath,
                StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var path = group.Key;
                var machineId = settings.MachineGroupOverrides
                        .GetValueOrDefault(path)
                    ?? DetectMachineId(path);
                var machineLabel = LabelFor(machineId, settings);
                return new MachineProjectGroup(
                    path,
                    ProjectLabel(path),
                    machineId,
                    machineLabel,
                    group.OrderByDescending(value => value.UpdatedAt)
                        .ToArray());
            })
            .ToArray();
        return projects
            .GroupBy(project => project.MachineId)
            .Select(group => new MachineGroupView(
                group.Key,
                LabelFor(group.Key, settings),
                group.OrderByDescending(project =>
                        project.Latest.UpdatedAt)
                    .ToArray()))
            .OrderByDescending(group => group.LatestAt)
            .ToArray();
    }

    public string LabelFor(string machineId, AppSettings settings)
    {
        var custom = settings.MachineGroupNames
            .GetValueOrDefault(machineId)?.Trim();
        return string.IsNullOrWhiteSpace(custom)
            ? DefaultLabels.GetValueOrDefault(machineId, machineId)
            : custom;
    }

    public static string DetectMachineId(string projectPath)
    {
        var normalized = projectPath.Replace('\\', '/');
        if (normalized.Length >= 3
            && char.IsLetter(normalized[0])
            && normalized[1] == ':'
            && normalized[2] == '/')
        {
            return "windows";
        }
        if (normalized.StartsWith("/Users/", StringComparison.Ordinal)
            || normalized.StartsWith("/Volumes/", StringComparison.Ordinal)
            || normalized.StartsWith(
                "/Applications/", StringComparison.Ordinal)
            || normalized.Equals(
                "/Applications", StringComparison.Ordinal))
        {
            return "macos";
        }
        if (normalized.StartsWith("/home/", StringComparison.Ordinal)
            || normalized.StartsWith("/root/", StringComparison.Ordinal)
            || normalized.StartsWith("/usr/", StringComparison.Ordinal)
            || normalized.StartsWith("/opt/", StringComparison.Ordinal)
            || normalized.StartsWith("/tmp/", StringComparison.Ordinal))
        {
            return "linux";
        }
        if (normalized.StartsWith(
            "chatmem://", StringComparison.OrdinalIgnoreCase))
        {
            return "internal";
        }
        return "other";
    }

    private static string ProjectLabel(string path)
    {
        var normalized = path.TrimEnd('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized)) return "未知项目";
        return normalized.Split('\\', '/').Last();
    }
}
