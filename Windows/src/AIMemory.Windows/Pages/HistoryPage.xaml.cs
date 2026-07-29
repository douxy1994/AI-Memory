using AIMemory.Core.Models;
using AIMemory.Core.Services;
using AIMemory.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace AIMemory.Windows.Pages;

public sealed partial class HistoryPage : Page
{
    private MainWindow? _window;
    private CancellationTokenSource? _searchCancellation;
    private CancellationTokenSource? _searchDebounce;
    private IReadOnlyList<CheckpointRecord> _checkpoints = [];

    public HistoryPage() => InitializeComponent();

    protected override async void OnNavigatedTo(NavigationEventArgs args)
    {
        _window = (MainWindow)args.Parameter;
        await ReloadAllAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs args)
    {
        _searchCancellation?.Cancel();
        _searchDebounce?.Cancel();
        base.OnNavigatedFrom(args);
    }

    private async Task ReloadAllAsync()
    {
        if (_window is null) return;
        await ReloadConversationsAsync();
        var memory = new MemoryGovernanceService(_window.Database);
        var recovery = new RecoveryService(_window.Database);
        var history = new HistoryProjectionService(_window.Database);
        var memoriesTask = memory.ListApprovedAsync();
        var checkpointsTask = recovery.ListCheckpointsAsync();
        var handoffsTask = recovery.ListHandoffsAsync();
        var runsTask = history.ListRunsAsync();
        var artifactsTask = history.ListArtifactsAsync();
        var episodesTask = history.ListEpisodesAsync();
        var wikiTask = history.ListWikiAsync();
        await Task.WhenAll(
            memoriesTask,
            checkpointsTask,
            handoffsTask,
            runsTask,
            artifactsTask,
            episodesTask,
            wikiTask);
        MemoryList.ItemsSource = await memoriesTask;
        _checkpoints = await checkpointsTask;
        CheckpointList.ItemsSource = _checkpoints;
        HandoffList.ItemsSource = await handoffsTask;
        RunList.ItemsSource = await runsTask;
        ArtifactList.ItemsSource = await artifactsTask;
        EpisodeList.ItemsSource = await episodesTask;
        WikiList.ItemsSource = await wikiTask;
    }

    private async Task ReloadConversationsAsync()
    {
        if (_window is null) return;
        _searchCancellation?.Cancel();
        _searchCancellation = new CancellationTokenSource();
        try
        {
            var items = await _window.Conversations.ListAsync(
                search: string.IsNullOrWhiteSpace(SearchBox.Text)
                    ? null
                    : SearchBox.Text,
                cancellationToken: _searchCancellation.Token);
            ConversationList.ItemsSource = items;
            EmptyText.Visibility = items.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        catch (OperationCanceledException)
        {
            // A newer search superseded this one.
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs args)
    {
        await ReloadAllAsync();
        Show(
            LocalizationService.Get("HistoryRefreshed"),
            InfoBarSeverity.Success);
    }

    private async void SearchBox_TextChanged(
        object sender,
        TextChangedEventArgs args)
    {
        _searchDebounce?.Cancel();
        _searchDebounce = new CancellationTokenSource();
        try
        {
            await Task.Delay(180, _searchDebounce.Token);
            await ReloadConversationsAsync();
        }
        catch (OperationCanceledException)
        {
            // Expected while the user is still typing.
        }
    }

    private void ConversationList_ItemClick(object sender, ItemClickEventArgs args)
    {
        if (_window is not null
            && args.ClickedItem is ConversationSummary conversation)
        {
            OpenConversation(conversation);
        }
    }

    private void MemoryList_ItemClick(object sender, ItemClickEventArgs args)
    {
        if (_window is not null) Frame.Navigate(typeof(MemoryPage), _window);
    }

    private async void CheckpointList_ItemClick(
        object sender,
        ItemClickEventArgs args)
    {
        if (args.ClickedItem is CheckpointRecord checkpoint)
        {
            await OpenConversationAsync(
                checkpoint.ConversationId,
                checkpoint.SourceAgent);
        }
    }

    private async void HandoffList_ItemClick(
        object sender,
        ItemClickEventArgs args)
    {
        if (args.ClickedItem is not HandoffRecord handoff) return;
        var checkpoint = _checkpoints.FirstOrDefault(
            value => value.Id == handoff.CheckpointId);
        if (checkpoint is null)
        {
            Show(
                LocalizationService.Get("HandoffSourceCheckpointUnavailable"),
                InfoBarSeverity.Warning);
            return;
        }
        await OpenConversationAsync(
            checkpoint.ConversationId,
            checkpoint.SourceAgent);
    }

    private async void RunList_ItemClick(
        object sender,
        ItemClickEventArgs args)
    {
        if (args.ClickedItem is AgentRunRecord run)
        {
            await OpenConversationAsync(
                HistoryProjectionService.ConversationIdForRun(run.Id),
                run.SourceAgent);
        }
    }

    private async void ArtifactList_ItemClick(
        object sender,
        ItemClickEventArgs args)
    {
        if (args.ClickedItem is ArtifactRecord artifact)
        {
            await OpenConversationAsync(
                HistoryProjectionService.ConversationIdForRun(artifact.RunId));
        }
    }

    private async void EpisodeList_ItemClick(
        object sender,
        ItemClickEventArgs args)
    {
        if (args.ClickedItem is EpisodeRecord episode)
        {
            await OpenConversationAsync(episode.SourceConversationId);
        }
    }

    private async void WikiList_ItemClick(
        object sender,
        ItemClickEventArgs args)
    {
        if (args.ClickedItem is not WikiRecord page) return;
        var body = new ScrollViewer
        {
            Content = new TextBlock
            {
                Text = page.Body,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            },
            MaxHeight = 520,
            MinWidth = 520,
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = page.Title,
            Content = body,
            CloseButtonText = LocalizationService.Get("Done"),
        };
        await dialog.ShowAsync();
    }

    private async Task OpenConversationAsync(
        string conversationId,
        string? sourceAgent = null)
    {
        if (_window is null) return;
        var candidateIds =
            HistoryProjectionService.ConversationIdCandidates(
                conversationId,
                sourceAgent);
        var items = await _window.Conversations.ListAsync(
            sourceAgent: sourceAgent,
            limit: 5_000);
        var conversation = items.FirstOrDefault(
            value => candidateIds.Contains(
                value.Id,
                StringComparer.Ordinal));
        if (conversation is null && sourceAgent is not null)
        {
            conversation = (await _window.Conversations.ListAsync(limit: 5_000))
                .FirstOrDefault(value => candidateIds.Contains(
                    value.Id,
                    StringComparer.Ordinal));
        }
        if (conversation is null)
        {
            Show(
                LocalizationService.Get("HistorySourceUnavailable"),
                InfoBarSeverity.Warning);
            return;
        }
        OpenConversation(conversation);
    }

    private void OpenConversation(ConversationSummary conversation)
    {
        if (_window is null) return;
        Frame.Navigate(
            typeof(ConversationPage),
            new ConversationNavigation(_window, conversation));
    }

    private async void TrashSelected_Click(object sender, RoutedEventArgs args)
    {
        if (_window is null
            || ConversationList.SelectedItem is not ConversationSummary conversation)
        {
            Show(
                LocalizationService.Get("SelectConversationToTrash"),
                InfoBarSeverity.Warning);
            return;
        }
        var settings = await _window.Settings.LoadAsync();
        await new TrashService(_window.Database).TrashAsync(
            conversation,
            settings.TrashRetentionDays);
        await ReloadConversationsAsync();
        Show(
            LocalizationService.Get("ConversationMovedToTrash"),
            InfoBarSeverity.Success);
    }

    private void Show(string message, InfoBarSeverity severity)
        => AIMemory.Windows.Services.FeedbackPresenter.Show(
            Feedback,
            message,
            severity);
}

public sealed record ConversationNavigation(
    MainWindow Window,
    ConversationSummary Conversation);
