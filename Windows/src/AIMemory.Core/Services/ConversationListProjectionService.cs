// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
using AIMemory.Core.Models;

namespace AIMemory.Core.Services;

public enum ConversationSortMode
{
    UpdatedDescending,
    CreatedDescending,
    TitleAscending,
}

public enum ConversationArrangeMode
{
    ByProject,
    Timeline,
    ChatsFirst,
}

public sealed record ConversationProjectFilter(
    string Key,
    string Label);

public sealed record ConversationProjectGroup(
    string Key,
    string Label,
    IReadOnlyList<ConversationSummary> Conversations)
{
    public DateTimeOffset LatestAt =>
        Conversations.Max(conversation => conversation.UpdatedAt);
    public int Count => Conversations.Count;
}

public static class ConversationListProjectionService
{
    public static IReadOnlyList<ConversationSummary> Apply(
        IEnumerable<ConversationSummary> conversations,
        string? sourceAgent,
        string? search,
        IReadOnlySet<string>? projectFilters,
        ConversationSortMode sortMode)
    {
        var query = conversations;
        if (!string.IsNullOrWhiteSpace(sourceAgent))
        {
            query = query.Where(conversation =>
                conversation.SourceAgent.Equals(
                    sourceAgent,
                    StringComparison.OrdinalIgnoreCase));
        }
        var normalizedSearch = search?.Trim();
        if (!string.IsNullOrWhiteSpace(normalizedSearch))
        {
            query = query.Where(conversation =>
                Contains(conversation.Summary, normalizedSearch)
                || Contains(conversation.RepoId, normalizedSearch)
                || Contains(conversation.ProjectPath, normalizedSearch));
        }

        if (projectFilters is { Count: > 0 })
        {
            var normalizedFilters = projectFilters.ToHashSet(
                StringComparer.OrdinalIgnoreCase);
            query = query.Where(conversation =>
                normalizedFilters.Contains(ProjectKey(conversation)));
        }

        return sortMode switch
        {
            ConversationSortMode.CreatedDescending => query
                .OrderByDescending(conversation => conversation.StartedAt)
                .ThenByDescending(conversation => conversation.UpdatedAt)
                .ThenBy(conversation => conversation.Summary,
                    StringComparer.CurrentCultureIgnoreCase)
                .ToArray(),
            ConversationSortMode.TitleAscending => query
                .OrderBy(conversation => conversation.Summary,
                    StringComparer.CurrentCultureIgnoreCase)
                .ThenByDescending(conversation => conversation.UpdatedAt)
                .ToArray(),
            _ => query
                .OrderByDescending(conversation => conversation.UpdatedAt)
                .ThenByDescending(conversation => conversation.StartedAt)
                .ThenBy(conversation => conversation.Summary,
                    StringComparer.CurrentCultureIgnoreCase)
                .ToArray(),
        };
    }

    public static IReadOnlyList<ConversationProjectFilter> Projects(
        IEnumerable<ConversationSummary> conversations) =>
        conversations
            .GroupBy(ProjectKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ConversationProjectFilter(
                group.Key,
                ProjectLabel(group.Key)))
            .OrderBy(project => project.Label,
                StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(project => project.Key,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static IReadOnlyList<ConversationProjectGroup> GroupByProject(
        IEnumerable<ConversationSummary> conversations) =>
        conversations
            .GroupBy(ProjectKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ConversationProjectGroup(
                group.Key,
                ProjectLabel(group.Key),
                group.ToArray()))
            .OrderByDescending(group => group.LatestAt)
            .ThenBy(group => group.Label,
                StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

    public static string ProjectKey(ConversationSummary conversation)
    {
        var value = string.IsNullOrWhiteSpace(conversation.ProjectPath)
            ? conversation.RepoId
            : conversation.ProjectPath;
        return value.Trim().TrimEnd('\\', '/');
    }

    public static string ProjectLabel(string projectKey)
    {
        var normalized = projectKey.Trim().TrimEnd('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized)) return "";
        return normalized.Split('\\', '/').Last();
    }

    private static bool Contains(string? value, string search) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Contains(search, StringComparison.CurrentCultureIgnoreCase);
}
