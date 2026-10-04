// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
using AIMemory.Core.Models;
using AIMemory.Core.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AIMemory.Windows;

/// <summary>
/// macOS-style persistent sidebar: per-agent source switcher, local-history
/// search, and a machine-grouped project/conversation tree above the page
/// navigation entries.
/// </summary>
public sealed partial class SidebarView : UserControl
{
    private sealed record SourceOption(string Id, string Label);

    private sealed record SidebarNode(
        string Title,
        string Subtitle,
        Visibility SubtitleVisibility,
        global::Windows.UI.Text.FontWeight TitleWeight,
        string CountLabel,
        Visibility CountVisibility,
        ConversationSummary? Conversation);

    private readonly MachineGroupingService _machineGrouping = new();
    private readonly DispatcherTimer _searchDebounce = new();
    private MainWindow? _window;
    private IReadOnlyList<ConversationSummary> _conversations = [];
    private AppSettings _settings = new();
    private readonly HashSet<string> _selectedConversationIds =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _projectFilters =
        new(StringComparer.OrdinalIgnoreCase);
    private ConversationArrangeMode _arrangeMode =
        ConversationArrangeMode.ByProject;
    private ConversationSortMode _sortMode =
        ConversationSortMode.UpdatedDescending;
    private bool _loadingSources;
    private bool _bulkSelectionMode;
    private bool _allProjectsCollapsed;

    public SidebarView()
    {
        InitializeComponent();
        _searchDebounce.Interval = TimeSpan.FromMilliseconds(350);
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            RebuildTree();
        };
        UpdateToolbar();
    }

    public void Attach(MainWindow window)
    {
        _window = window;
    }

    public async Task ReloadAsync()
    {
        if (_window is null) return;
        _conversations = await _window.Conversations.ListAsync(limit: 5_000);
        _settings = await _window.Settings.LoadAsync();
        ReloadSourceOptions();
        RebuildTree();
    }

    private void ReloadSourceOptions()
    {
        var selectedId = (SidebarSourceBox.SelectedItem as SourceOption)?.Id
            ?? "all";
        var options = new[]
            {
                new SourceOption(
                    "all",
                    Services.LocalizationService.Get("AllSources")),
            }
            .Concat(_conversations
                .Select(value => value.SourceAgent)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .Select(value => new SourceOption(value, value)))
            .ToArray();
        _loadingSources = true;
        SidebarSourceBox.ItemsSource = options;
        SidebarSourceBox.SelectedItem = options.FirstOrDefault(
            value => value.Id == selectedId) ?? options[0];
        _loadingSources = false;
    }

    private IReadOnlyList<ConversationSummary> FilteredConversations()
    {
        var source = (SidebarSourceBox.SelectedItem as SourceOption)?.Id
            ?? "all";
        var query = SidebarSearchBox.Text?.Trim() ?? "";
        return ConversationListProjectionService.Apply(
            _conversations,
            source == "all" ? null : source,
            query,
            _projectFilters,
            _sortMode);
    }

    private void RebuildTree()
    {
        var filtered = FilteredConversations().ToArray();
        SidebarConversationCount.Text =
            ConversationListProjectionService.Projects(filtered)
                .Count.ToString();
        var roots = ProjectTree.RootNodes;
        roots.Clear();
        if (_arrangeMode == ConversationArrangeMode.Timeline)
        {
            foreach (var conversation in filtered.Take(500))
            {
                roots.Add(ConversationNode(conversation));
            }
            UpdateToolbar();
            return;
        }

        var groups = _machineGrouping.Build(filtered, _settings);
        foreach (var group in groups)
        {
            var machineNode = new TreeViewNode
            {
                Content = new SidebarNode(
                    group.Label,
                    "",
                    Visibility.Collapsed,
                    FontWeights.SemiBold,
                    group.ConversationCount.ToString(),
                    Visibility.Visible,
                    null),
                IsExpanded = !_allProjectsCollapsed,
            };
            foreach (var project in group.Projects
                .OrderByDescending(value => value.Latest.UpdatedAt))
            {
                var projectNode = new TreeViewNode
                {
                    Content = new SidebarNode(
                        project.Label,
                        project.Path,
                        Visibility.Visible,
                        FontWeights.Normal,
                        project.Count.ToString(),
                        Visibility.Visible,
                        null),
                    IsExpanded = !_allProjectsCollapsed,
                };
                foreach (var conversation in project.Conversations.Take(30))
                {
                    projectNode.Children.Add(ConversationNode(conversation));
                }
                machineNode.Children.Add(projectNode);
            }
            roots.Add(machineNode);
        }
        UpdateToolbar();
    }

    private TreeViewNode ConversationNode(ConversationSummary conversation)
    {
        var marker = _bulkSelectionMode
            ? _selectedConversationIds.Contains(conversation.Id)
                ? "☑ "
                : "☐ "
            : "";
        return new TreeViewNode
        {
            Content = new SidebarNode(
                marker + conversation.Summary,
                conversation.SourceAgent
                    + " · "
                    + conversation.UpdatedAt.LocalDateTime
                        .ToString("MM-dd HH:mm"),
                Visibility.Visible,
                FontWeights.Normal,
                "",
                Visibility.Collapsed,
                conversation),
        };
    }

    private void SidebarSourceBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs args)
    {
        if (_loadingSources) return;
        RebuildTree();
    }

    private void SidebarSearchBox_TextChanged(
        object sender,
        TextChangedEventArgs args)
    {
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private void ProjectTree_ItemInvoked(
        TreeView sender,
        TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is TreeViewNode node
            && node.Content is SidebarNode item
            && item.Conversation is not null)
        {
            if (_bulkSelectionMode)
            {
                if (!_selectedConversationIds.Add(item.Conversation.Id))
                {
                    _selectedConversationIds.Remove(item.Conversation.Id);
                }
                RebuildTree();
                return;
            }
            _window?.OpenConversationFromSidebar(item.Conversation);
        }
    }

    private void BulkSelect_Click(object sender, RoutedEventArgs args)
    {
        _bulkSelectionMode = !_bulkSelectionMode;
        if (!_bulkSelectionMode) _selectedConversationIds.Clear();
        RebuildTree();
    }

    private async void TrashSelected_Click(
        object sender,
        RoutedEventArgs args)
    {
        if (_window is null || _selectedConversationIds.Count == 0) return;
        var conversations = _conversations
            .Where(value => _selectedConversationIds.Contains(value.Id))
            .ToArray();
        var confirmation = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Services.LocalizationService.Format(
                "MoveConversationsToTrashQuestion",
                conversations.Length),
            Content = Services.LocalizationService.Format(
                "MoveConversationsToTrashDescription",
                _settings.TrashRetentionDays),
            PrimaryButtonText = Services.LocalizationService.Get(
                "MoveToTrashRecoverable"),
            CloseButtonText = Services.LocalizationService.Get("Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        TrashSelectedButton.IsEnabled = false;
        try
        {
            var result = await new TrashService(_window.Database)
                .TrashManyAsync(conversations, _settings.TrashRetentionDays);
            _selectedConversationIds.Clear();
            _bulkSelectionMode = false;
            await ReloadAsync();
            _window.ShowFeedback(
                result.FailedConversationIds.Count == 0
                    ? Services.LocalizationService.Format(
                        "ConversationsMovedToTrash",
                        result.Moved)
                    : Services.LocalizationService.Format(
                        "BulkTrashCompletedWithFailures",
                        result.Moved,
                        result.FailedConversationIds.Count),
                result.FailedConversationIds.Count == 0
                    ? InfoBarSeverity.Success
                    : InfoBarSeverity.Warning);
        }
        catch (Exception exception)
        {
            UpdateToolbar();
            _window.ShowFeedback(
                Services.LocalizationService.Format(
                    "MoveToTrashFailed",
                    exception.Message),
                InfoBarSeverity.Error);
        }
    }

    private void CollapseProjects_Click(object sender, RoutedEventArgs args)
    {
        _allProjectsCollapsed = !_allProjectsCollapsed;
        RebuildTree();
    }

    private async void ManageMachineGroups_Click(
        object sender,
        RoutedEventArgs args)
    {
        if (_window is not null)
        {
            await _window.OpenMachineGroupManagerAsync();
            await ReloadAsync();
        }
    }

    private void OrganizeMenu_Opening(object sender, object args)
    {
        OrganizeMenu.Items.Clear();
        var arrange = new MenuFlyoutSubItem
        {
            Text = Services.LocalizationService.Get("Arrange"),
        };
        AddArrangeOption(
            arrange,
            "ArrangeByProject",
            ConversationArrangeMode.ByProject);
        AddArrangeOption(
            arrange,
            "ArrangeTimeline",
            ConversationArrangeMode.Timeline);
        AddArrangeOption(
            arrange,
            "ArrangeChatsFirst",
            ConversationArrangeMode.ChatsFirst);
        OrganizeMenu.Items.Add(arrange);

        var sort = new MenuFlyoutSubItem
        {
            Text = Services.LocalizationService.Get("Sort"),
        };
        AddSortOption(
            sort,
            "SortRecentlyUpdated",
            ConversationSortMode.UpdatedDescending);
        AddSortOption(
            sort,
            "SortRecentlyCreated",
            ConversationSortMode.CreatedDescending);
        AddSortOption(
            sort,
            "SortByTitle",
            ConversationSortMode.TitleAscending);
        OrganizeMenu.Items.Add(sort);

        var projects = new MenuFlyoutSubItem
        {
            Text = Services.LocalizationService.Get("FilterProjects"),
        };
        var all = new MenuFlyoutItem
        {
            Text = Services.LocalizationService.Get(
                _projectFilters.Count == 0
                    ? "AllProjectsSelected"
                    : "ShowAllProjects"),
        };
        all.Click += (_, _) =>
        {
            _projectFilters.Clear();
            RebuildTree();
        };
        projects.Items.Add(all);
        foreach (var project in ConversationListProjectionService.Projects(
            _conversations))
        {
            var option = new ToggleMenuFlyoutItem
            {
                Text = project.Label,
                IsChecked = _projectFilters.Contains(project.Key),
                Tag = project.Key,
            };
            option.Click += ProjectFilter_Click;
            projects.Items.Add(option);
        }
        OrganizeMenu.Items.Add(projects);
    }

    private void AddArrangeOption(
        MenuFlyoutSubItem parent,
        string resource,
        ConversationArrangeMode mode)
    {
        var option = new RadioMenuFlyoutItem
        {
            Text = Services.LocalizationService.Get(resource),
            GroupName = "SidebarArrange",
            IsChecked = _arrangeMode == mode,
            Tag = mode,
        };
        option.Click += (_, _) =>
        {
            _arrangeMode = mode;
            RebuildTree();
        };
        parent.Items.Add(option);
    }

    private void AddSortOption(
        MenuFlyoutSubItem parent,
        string resource,
        ConversationSortMode mode)
    {
        var option = new RadioMenuFlyoutItem
        {
            Text = Services.LocalizationService.Get(resource),
            GroupName = "SidebarSort",
            IsChecked = _sortMode == mode,
            Tag = mode,
        };
        option.Click += (_, _) =>
        {
            _sortMode = mode;
            RebuildTree();
        };
        parent.Items.Add(option);
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
        RebuildTree();
    }

    private void UpdateToolbar()
    {
        BulkSelectIcon.Source = SidebarIcon(
            _bulkSelectionMode
                ? "sidebar-cancel.svg"
                : "sidebar-select.svg");
        TrashSelectedButton.Visibility = _bulkSelectionMode
            ? Visibility.Visible
            : Visibility.Collapsed;
        TrashSelectedButton.IsEnabled = _selectedConversationIds.Count > 0;
        CollapseProjectsIcon.Source = SidebarIcon(
            _allProjectsCollapsed
                ? "sidebar-expand.svg"
                : "sidebar-collapse.svg");
        ManageMachineGroupsButton.IsEnabled =
            ConversationListProjectionService.Projects(_conversations)
                .Count > 0;
        SetHelp(
            BulkSelectButton,
            _bulkSelectionMode
                ? "SidebarCancelSelection"
                : "SidebarBeginSelection");
        SetHelp(TrashSelectedButton, "SidebarTrashSelected");
        SetHelp(
            CollapseProjectsButton,
            _allProjectsCollapsed
                ? "SidebarExpandAll"
                : "SidebarCollapseAll");
        SetHelp(ManageMachineGroupsButton, "ManageComputerGroups");
        SetHelp(OrganizeButton, "SidebarOrganize");
    }

    private static void SetHelp(Button button, string resource)
    {
        var label = Services.LocalizationService.Get(resource);
        ToolTipService.SetToolTip(button, label);
        AutomationProperties.SetName(button, label);
    }

    private static SvgImageSource SidebarIcon(string fileName) =>
        new(new Uri($"ms-appx:///Assets/Icons/{fileName}"));

    private void NavWorkbench_Click(object sender, RoutedEventArgs args) =>
        _window?.NavigateTo("workbench");

    private void NavHistory_Click(object sender, RoutedEventArgs args) =>
        _window?.NavigateTo("history");

    private void NavMemory_Click(object sender, RoutedEventArgs args) =>
        _window?.NavigateTo("memory");

    private void NavFavorites_Click(object sender, RoutedEventArgs args) =>
        _window?.NavigateTo("favorites");

    private void NavTrash_Click(object sender, RoutedEventArgs args) =>
        _window?.NavigateTo("trash");

    private void NavSettings_Click(object sender, RoutedEventArgs args) =>
        _window?.NavigateTo("settings");
}
