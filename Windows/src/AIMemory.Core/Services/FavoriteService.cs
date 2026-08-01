// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
using AIMemory.Core.Models;
using AIMemory.Core.Persistence;

namespace AIMemory.Core.Services;

public sealed class FavoriteService(SettingsStore settingsStore)
{
    public async Task<bool> IsFavoriteAsync(
        string sourceAgent,
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        var settings = await settingsStore.LoadAsync(cancellationToken);
        return Find(settings, sourceAgent, conversationId) is not null;
    }

    public async Task<bool> ToggleAsync(
        ConversationSummary conversation,
        string projectPath,
        CancellationToken cancellationToken = default)
    {
        var settings = await settingsStore.LoadAsync(cancellationToken);
        var key = Key(conversation.SourceAgent, conversation.Id);
        var existing = FindKey(settings, conversation.SourceAgent, conversation.Id);
        if (existing is not null)
        {
            settings.FavoriteConversations.Remove(existing);
            await settingsStore.SaveAsync(settings, cancellationToken);
            return false;
        }

        settings.FavoriteConversations[key] = new FavoriteConversationSnapshot(
            conversation.Id,
            conversation.SourceAgent,
            string.IsNullOrWhiteSpace(conversation.Summary)
                ? "未命名对话"
                : conversation.Summary,
            projectPath,
            conversation.UpdatedAt);
        await settingsStore.SaveAsync(settings, cancellationToken);
        return true;
    }

    public async Task UpdateAsync(
        string sourceAgent,
        string conversationId,
        string note,
        IEnumerable<string> tags,
        bool? pinned = null,
        CancellationToken cancellationToken = default)
    {
        var settings = await settingsStore.LoadAsync(cancellationToken);
        var existingKey = FindKey(settings, sourceAgent, conversationId)
            ?? throw new KeyNotFoundException("找不到收藏记录。");
        var current = settings.FavoriteConversations[existingKey];
        var cleanTags = tags
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        settings.FavoriteConversations.Remove(existingKey);
        settings.FavoriteConversations[Key(sourceAgent, conversationId)] =
            current with
            {
                Note = note.Trim(),
                Tags = cleanTags,
                Pinned = pinned ?? current.Pinned,
            };
        await settingsStore.SaveAsync(settings, cancellationToken);
    }

    public async Task RemoveAsync(
        string sourceAgent,
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        var settings = await settingsStore.LoadAsync(cancellationToken);
        var existingKey = FindKey(settings, sourceAgent, conversationId);
        if (existingKey is null) return;
        settings.FavoriteConversations.Remove(existingKey);
        await settingsStore.SaveAsync(settings, cancellationToken);
    }

    public static string ContinuationCard(FavoriteConversationSnapshot favorite)
    {
        var lines = new List<string>
        {
            "# Favorite Continuation Card",
            "",
            $"title: {favorite.Title}",
            $"source: {favorite.SourceAgent}",
            $"conversation: {favorite.Id}",
            $"project: {(string.IsNullOrWhiteSpace(favorite.ProjectPath) ? "--" : favorite.ProjectPath)}",
            $"updated: {favorite.UpdatedAt:O}",
        };
        if (favorite.Pinned) lines.Add("priority: pinned");
        if (favorite.Tags is { Count: > 0 })
        {
            lines.Add($"tags: {string.Join(", ", favorite.Tags)}");
        }
        if (!string.IsNullOrWhiteSpace(favorite.Note))
        {
            lines.Add($"note: {favorite.Note.Trim()}");
        }
        lines.Add("");
        lines.Add(
            "Use AI Memory to reopen this favorite, load the source-backed conversation, and continue from the latest useful state.");
        return string.Join(Environment.NewLine, lines);
    }

    private static FavoriteConversationSnapshot? Find(
        AppSettings settings,
        string sourceAgent,
        string conversationId)
    {
        var key = FindKey(settings, sourceAgent, conversationId);
        return key is null ? null : settings.FavoriteConversations[key];
    }

    private static string? FindKey(
        AppSettings settings,
        string sourceAgent,
        string conversationId)
    {
        var scoped = Key(sourceAgent, conversationId);
        if (settings.FavoriteConversations.ContainsKey(scoped)) return scoped;
        return settings.FavoriteConversations.ContainsKey(conversationId)
            ? conversationId
            : null;
    }

    private static string Key(string sourceAgent, string conversationId) =>
        $"{sourceAgent}:{conversationId}";
}
