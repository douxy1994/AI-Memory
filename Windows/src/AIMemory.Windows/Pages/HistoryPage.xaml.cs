using AIMemory.Core.Models;
using AIMemory.Core.Services;
using AIMemory.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Navigation;

namespace AIMemory.Windows.Pages;

public sealed partial class HistoryPage : Page
{
    private MainWindow? _window;
    private CancellationTokenSource? _searchCancellation;
    private CancellationTokenSource? _searchDebounce;
    private IReadOnlyList<CheckpointRecord> _checkpoints = [];
    private IReadOnlyList<EpisodeRecord> _episodes = [];
    private IReadOnlyList<WikiRecord> _wikiPages = [];
    private IReadOnlyList<ConversationSummary> _allConversations = [];
    private IReadOnlyList<ConversationProjectFilter> _projects = [];
    private readonly HashSet<string> _projectFilters =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly CollectionViewSource _conversationGroupsView = new()
    {
        IsSourceGrouped = true,
    };
    private bool _loadingSources;
    private bool _loadingArrange;
    private bool _loadingSort;
    private bool _bulkSelectionMode;

    public HistoryPage()
    {
        InitializeComponent();
        var arrangeOptions = new[]
        {
            new LocalizedOption(
                ConversationArrangeMode.ByProject.ToString(),
                LocalizationService.Get("ArrangeByProject")),
            new LocalizedOption(
                ConversationArrangeMode.Timeline.ToString(),
                LocalizationService.Get("ArrangeTimeline")),
        };
        _loadingArrange = true;
        ArrangeBox.ItemsSource = arrangeOptions;
        ArrangeBox.SelectedIndex = 0;
        _loadingArrange = false;
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
        var governance = new RepositoryGovernanceService(_window.Database);
        var knowledge = new KnowledgeProjectionService(
            _window.Database,
            governance);
        var memoriesTask = memory.ListApprovedAsync();
        var checkpointsTask = recovery.ListCheckpointsAsync();
        var handoffsTask = recovery.ListHandoffsAsync();
        var runsTask = history.ListRunsAsync();
        var artifactsTask = history.ListArtifactsAsync();
        var episodesTask = history.ListEpisodesAsync();
        var wikiTask = history.ListWikiAsync();
        var repositoriesTask = governance.ListRepositoriesAsync();
        await Task.WhenAll(
            memoriesTask,
            checkpointsTask,
            handoffsTask,
            runsTask,
            artifactsTask,
            episodesTask,
            wikiTask,
            repositoriesTask);
        MemoryList.ItemsSource = await memoriesTask;
        _checkpoints = await checkpointsTask;
        CheckpointList.ItemsSource = _checkpoints;
        HandoffList.ItemsSource = await handoffsTask;
        RunList.ItemsSource = await runsTask;
        ArtifactList.ItemsSource = await artifactsTask;
        _episodes = await episodesTask;
        EpisodeList.ItemsSource = _episodes;
        _wikiPages = await wikiTask;
        WikiList.ItemsSource = _wikiPages;

        var graphTasks = (await repositoriesTask)
            .Select(async repository => (
                Repository: repository,
                Graph: await knowledge.ListEntityGraphAsync(
                    repository.Root,
                    100)))
            .ToArray();
        var graphs = graphTasks.Length == 0
            ? []
            : await Task.WhenAll(graphTasks);
        var rows = graphs
            .SelectMany(value => EntityGraphRow.Create(
                value.Repository.Root,
                value.Graph))
            .ToArray();
        EntityList.ItemsSource = rows;
        NoEntitiesText.Visibility = rows.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
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
            ReloadSourceOptions();
            ReloadProjectFilters();
            ApplyConversationProjection();
        }
        catch (OperationCanceledException)
        {
            // A newer refresh superseded this one.
        }
    }

    private void ReloadSourceOptions()
    {
        var selectedId = (SourceBox.SelectedItem as LocalizedOption)?.Id
            ?? "all";
        var options = new[]
            {
                new LocalizedOption(
                    "all",
                    LocalizationService.Get("AllSources")),
            }
            .Concat(_allConversations
                .Select(conversation => conversation.SourceAgent)
                .Where(source => !string.IsNullOrWhiteSpace(source))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(source => source, StringComparer.OrdinalIgnoreCase)
                .Select(source => new LocalizedOption(source, source)))
            .ToArray();
        _loadingSources = true;
        SourceBox.ItemsSource = options;
        SourceBox.SelectedItem = options.FirstOrDefault(option =>
            option.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase))
            ?? options[0];
        _loadingSources = false;
    }

    private void ReloadProjectFilters()
    {
        var sourceAgent = SelectedSourceAgent();
        var sourceConversations = sourceAgent is null
            ? _allConversations
            : _allConversations
                .Where(conversation => conversation.SourceAgent.Equals(
                    sourceAgent,
                    StringComparison.OrdinalIgnoreCase));
        _projects = ConversationListProjectionService.Projects(
            sourceConversations);
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
            SelectedSourceAgent(),
            SearchBox.Text,
            _projectFilters,
            sortMode);
        var arrangeMode = Enum.TryParse<ConversationArrangeMode>(
            (ArrangeBox.SelectedItem as LocalizedOption)?.Id,
            out var selectedArrange)
            ? selectedArrange
            : ConversationArrangeMode.ByProject;
        if (arrangeMode == ConversationArrangeMode.ByProject)
        {
            _conversationGroupsView.Source =
                ConversationListProjectionService
                    .GroupByProject(items)
                    .Select(group => new ConversationProjectGroupView(group))
                    .ToArray();
            ConversationList.ItemsSource = _conversationGroupsView.View;
        }
        else
        {
            _conversationGroupsView.Source = null;
            ConversationList.ItemsSource = items;
        }
        EmptyText.Visibility = items.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateSelectionActions();
    }

    private string? SelectedSourceAgent()
    {
        var id = (SourceBox.SelectedItem as LocalizedOption)?.Id;
        return string.IsNullOrWhiteSpace(id)
            || id.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? null
            : id;
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

    private void ArrangeBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs args)
    {
        if (!_loadingArrange) ApplyConversationProjection();
    }

    private void SourceBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs args)
    {
        if (_loadingSources) return;
        ReloadProjectFilters();
        ApplyConversationProjection();
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
        if (args.ClickedItem is WikiRecord page)
        {
            await ShowWikiPageAsync(page);
        }
    }

    private async Task ShowWikiPageAsync(WikiRecord page)
    {
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

    private async void EntityList_ItemClick(
        object sender,
        ItemClickEventArgs args)
    {
        if (args.ClickedItem is not EntityGraphRow row
            || string.IsNullOrWhiteSpace(row.OwnerType))
        {
            return;
        }
        switch (row.OwnerType)
        {
            case "chunk":
                if (!string.IsNullOrWhiteSpace(row.SourceConversationId))
                {
                    await OpenConversationAsync(row.SourceConversationId);
                }
                else
                {
                    Show(
                        LocalizationService.Get(
                            "EntitySourceConversationUnavailable"),
                        InfoBarSeverity.Warning);
                }
                break;
            case "conversation":
                await OpenConversationAsync(row.OwnerId ?? "");
                break;
            case "episode":
                var episode = _episodes.FirstOrDefault(value =>
                    value.Id == row.OwnerId);
                if (episode is null)
                {
                    Show(
                        LocalizationService.Get(
                            "EntitySourceConversationUnavailable"),
                        InfoBarSeverity.Warning);
                }
                else
                {
                    await OpenConversationAsync(
                        episode.SourceConversationId);
                }
                break;
            case "memory":
                if (_window is not null)
                {
                    Frame.Navigate(typeof(MemoryPage), _window);
                }
                break;
            case "wiki_page":
                var wiki = _wikiPages.FirstOrDefault(value =>
                    value.Id == row.OwnerId);
                if (wiki is null)
                {
                    Show(
                        LocalizationService.Get("EntityWikiUnavailable"),
                        InfoBarSeverity.Warning);
                }
                else
                {
                    await ShowWikiPageAsync(wiki);
                }
                break;
            default:
                Show(
                    LocalizationService.Get("EntityLinkUnsupported"),
                    InfoBarSeverity.Warning);
                break;
        }
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

public sealed class ConversationProjectGroupView
    : List<ConversationSummary>
{
    public ConversationProjectGroupView(ConversationProjectGroup group)
        : base(group.Conversations)
    {
        Key = group.Key;
        Label = string.IsNullOrWhiteSpace(group.Label)
            ? LocalizationService.Get("UnknownProject")
            : group.Label;
        Path = string.IsNullOrWhiteSpace(group.Key)
            ? LocalizationService.Get("UnknownProject")
            : group.Key;
    }

    public string Key { get; }
    public string Label { get; }
    public string Path { get; }
}

public sealed record EntityGraphRow(
    string RepositoryRoot,
    string EntityName,
    string Kind,
    int MentionCount,
    string? OwnerType,
    string? OwnerId,
    string? Relationship,
    string? SourceTitle,
    string? SourceConversationId)
{
    public string MentionLabel => LocalizationService.Format(
        "EntityMentionCount",
        MentionCount);

    public string RelationshipLabel =>
        string.IsNullOrWhiteSpace(OwnerType)
            ? LocalizationService.Get("EntityWithoutLinks")
            : LocalizationService.Format(
                "EntityRelationship",
                Relationship ?? "",
                SourceTitle ?? OwnerId ?? "");

    public Visibility ActionVisibility =>
        string.IsNullOrWhiteSpace(OwnerType)
            ? Visibility.Collapsed
            : Visibility.Visible;

    public static IReadOnlyList<EntityGraphRow> Create(
        string repositoryRoot,
        MemoryEntityGraph graph)
    {
        var rows = new List<EntityGraphRow>();
        foreach (var entity in graph.Entities)
        {
            var links = graph.Links
                .Where(value => value.EntityId == entity.Id)
                .ToArray();
            if (links.Length == 0)
            {
                rows.Add(new EntityGraphRow(
                    repositoryRoot,
                    entity.Name,
                    entity.Kind,
                    entity.MentionCount,
                    null,
                    null,
                    null,
                    null,
                    null));
                continue;
            }
            rows.AddRange(links.Select(link => new EntityGraphRow(
                repositoryRoot,
                entity.Name,
                entity.Kind,
                entity.MentionCount,
                link.OwnerType,
                link.OwnerId,
                link.Relationship,
                link.SourceTitle,
                link.SourceConversationId)));
        }
        return rows;
    }
}
