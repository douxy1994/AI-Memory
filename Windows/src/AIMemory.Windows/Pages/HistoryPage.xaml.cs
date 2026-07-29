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
    private IReadOnlyList<ConversationSummary> _allConversations = [];
    private IReadOnlyList<ConversationProjectFilter> _projects = [];
    private readonly HashSet<string> _projectFilters =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _loadingSort;
    private bool _bulkSelectionMode;

    public HistoryPage()
    {
        InitializeComponent();
        var options = new[]
        {
            new LocalizedOption(
                ConversationSortMode.UpdatedDescending.ToString(),
                LocalizationService.Get("SortRecentlyUpdated")),
            new LocalizedOption(
                ConversationSortMode.CreatedDescending.ToString(),
                LocalizationService.Get("SortRecentlyCreated")),
            new LocalizedOption(
                ConversationSortMode.TitleAscending.ToString(),
                LocalizationService.Get("SortByTitle")),
        };
        _loadingSort = true;
        SortBox.ItemsSource = options;
        SortBox.SelectedIndex = 0;
        _loadingSort = false;
    }

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
        var cancellation = new CancellationTokenSource();
        _searchCancellation = cancellation;
        try
        {
            var items = await _window.Conversations.ListAsync(
                limit: 5_000,
                cancellationToken: cancellation.Token);
            if (cancellation.IsCancellationRequested) return;
            _allConversations = items;
            ReloadProjectFilters();
            ApplyConversationProjection();
        }
        catch (OperationCanceledException)
        {
            // A newer refresh superseded this one.
        }
    }

    private void ReloadProjectFilters()
    {
        _projects = ConversationListProjectionService.Projects(
            _allConversations);
        var validKeys = _projects
            .Select(project => project.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _projectFilters.RemoveWhere(key => !validKeys.Contains(key));

        var flyout = new MenuFlyout();
        var allProjects = new ToggleMenuFlyoutItem
        {
            Text = LocalizationService.Get("AllProjects"),
            IsChecked = _projectFilters.Count == 0,
        };
        allProjects.Click += (_, _) =>
        {
            _projectFilters.Clear();
            ReloadProjectFilters();
            ApplyConversationProjection();
        };
        flyout.Items.Add(allProjects);
        if (_projects.Count > 0)
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
        }
        foreach (var project in _projects)
        {
            var option = new ToggleMenuFlyoutItem
            {
                Text = string.IsNullOrWhiteSpace(project.Label)
                    ? LocalizationService.Get("UnknownProject")
                    : project.Label,
                IsChecked = _projectFilters.Contains(project.Key),
                Tag = project.Key,
            };
            option.Click += ProjectFilter_Click;
            flyout.Items.Add(option);
        }
        ProjectFilterButton.Flyout = flyout;
        ProjectFilterButton.Content = _projectFilters.Count == 0
            ? LocalizationService.Get("AllProjects")
            : LocalizationService.Format(
                "ProjectsSelected",
                _projectFilters.Count);
    }

    private void ProjectFilter_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not ToggleMenuFlyoutItem option
            || option.Tag is not string key)
        {
            return;
        }
        if (option.IsChecked)
        {
            _projectFilters.Add(key);
        }
        else
        {
            _projectFilters.Remove(key);
        }
        ReloadProjectFilters();
        ApplyConversationProjection();
    }

    private void ApplyConversationProjection()
    {
        var sortMode = Enum.TryParse<ConversationSortMode>(
            (SortBox.SelectedItem as LocalizedOption)?.Id,
            out var selectedSort)
            ? selectedSort
            : ConversationSortMode.UpdatedDescending;
        ConversationList.SelectedItems.Clear();
        var items = ConversationListProjectionService.Apply(
            _allConversations,
            SearchBox.Text,
            _projectFilters,
            sortMode);
        ConversationList.ItemsSource = items;
        EmptyText.Visibility = items.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateSelectionActions();
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
            ApplyConversationProjection();
        }
        catch (OperationCanceledException)
        {
            // Expected while the user is still typing.
        }
    }

    private void SortBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs args)
    {
        if (!_loadingSort) ApplyConversationProjection();
    }

    private void ConversationList_ItemClick(object sender, ItemClickEventArgs args)
    {
        if (!_bulkSelectionMode
            && _window is not null
            && args.ClickedItem is ConversationSummary conversation)
        {
            OpenConversation(conversation);
        }
    }

    private void BeginSelection_Click(object sender, RoutedEventArgs args)
    {
        _bulkSelectionMode = true;
        ConversationList.SelectionMode = ListViewSelectionMode.Multiple;
        ConversationList.IsItemClickEnabled = false;
        BeginSelectionButton.Visibility = Visibility.Collapsed;
        CancelSelectionButton.Visibility = Visibility.Visible;
        TrashSelectedButton.Visibility = Visibility.Visible;
        UpdateSelectionActions();
    }

    private void CancelSelection_Click(object sender, RoutedEventArgs args) =>
        ExitBulkSelection();

    private void ConversationList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs args) =>
        UpdateSelectionActions();

    private void ExitBulkSelection()
    {
        ConversationList.SelectedItems.Clear();
        ConversationList.SelectionMode = ListViewSelectionMode.None;
        ConversationList.IsItemClickEnabled = true;
        _bulkSelectionMode = false;
        BeginSelectionButton.Visibility = Visibility.Visible;
        CancelSelectionButton.Visibility = Visibility.Collapsed;
        TrashSelectedButton.Visibility = Visibility.Collapsed;
        UpdateSelectionActions();
    }

    private void UpdateSelectionActions()
    {
        var count = ConversationList.SelectedItems.Count;
        TrashSelectedButton.IsEnabled = count > 0;
        TrashSelectedButton.Label = count == 0
            ? LocalizationService.Get("MoveToTrash")
            : LocalizationService.Format(
                "HistoryTrashSelectedCount",
                count);
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
        if (_window is null) return;
        var conversations = ConversationList.SelectedItems
            .OfType<ConversationSummary>()
            .ToArray();
        if (conversations.Length == 0)
        {
            Show(
                LocalizationService.Get("SelectConversationToTrash"),
                InfoBarSeverity.Warning);
            return;
        }
        AppSettings settings;
        try
        {
            settings = await _window.Settings.LoadAsync();
        }
        catch (Exception error)
        {
            Show(
                LocalizationService.Format(
                    "MoveToTrashFailed",
                    error.Message),
                InfoBarSeverity.Error);
            return;
        }
        var confirmation = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = LocalizationService.Format(
                "MoveConversationsToTrashQuestion",
                conversations.Length),
            Content = LocalizationService.Format(
                "MoveConversationsToTrashDescription",
                settings.TrashRetentionDays),
            PrimaryButtonText = LocalizationService.Get(
                "MoveToTrashRecoverable"),
            CloseButtonText = LocalizationService.Get("Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        TrashSelectedButton.IsEnabled = false;
        CancelSelectionButton.IsEnabled = false;
        var service = new TrashService(_window.Database);
        BulkTrashResult result;
        try
        {
            result = await service.TrashManyAsync(
                conversations,
                settings.TrashRetentionDays);
        }
        catch (Exception error)
        {
            CancelSelectionButton.IsEnabled = true;
            UpdateSelectionActions();
            Show(
                LocalizationService.Format(
                    "MoveToTrashFailed",
                    error.Message),
                InfoBarSeverity.Error);
            return;
        }
        CancelSelectionButton.IsEnabled = true;
        ExitBulkSelection();
        await ReloadConversationsAsync();
        if (result.FailedConversationIds.Count == 0)
        {
            Show(
                LocalizationService.Format(
                    "ConversationsMovedToTrash",
                    result.Moved),
                InfoBarSeverity.Success);
            return;
        }
        Show(
            LocalizationService.Format(
                "BulkTrashCompletedWithFailures",
                result.Moved,
                result.FailedConversationIds.Count),
            result.Moved == 0
                ? InfoBarSeverity.Error
                : InfoBarSeverity.Warning);
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
