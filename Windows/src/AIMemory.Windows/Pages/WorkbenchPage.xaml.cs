using AIMemory.Core.Models;
using AIMemory.Core.Services;
using AIMemory.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace AIMemory.Windows.Pages;

public sealed partial class WorkbenchPage : Page
{
    private MainWindow? _window;
    private IReadOnlyList<ConversationSummary> _allConversations = [];
    private AppSettings _settings = new();
    private readonly MachineGroupingService _machineGrouping = new();
    private bool _loadingSources;

    public WorkbenchPage() => InitializeComponent();

    protected override async void OnNavigatedTo(NavigationEventArgs args)
    {
        _window = (MainWindow)args.Parameter;
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        if (_window is null) return;
        _allConversations = await _window.Conversations.ListAsync(limit: 5_000);
        _settings = await _window.Settings.LoadAsync();
        AllConversationCount.Text = _allConversations.Count.ToString();
        ReloadSourceOptions();
        ReloadConversationSections();

        var agents = new AgentCatalog().Detect();
        var detected = agents.Where(value => value.IsDetected).ToArray();
        AgentList.ItemsSource = detected
            .Select(value => new LocalizedAgentIntegration(value))
            .ToArray();
        DetectedAgentSummary.Text = LocalizationService.Format(
            "DetectedAgentSummary",
            detected.Length,
            agents.Count);
        NoAgentsText.Visibility = detected.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        await using var connection = _window.Database.OpenConnection();
        PendingCandidateCount.Text = (await CountAsync(
            connection,
            "SELECT COUNT(*) FROM memory_candidates WHERE status='pending_review';"))
            .ToString();
        CheckpointCount.Text = (await CountAsync(
            connection,
            "SELECT COUNT(*) FROM checkpoints;"))
            .ToString();
    }

    private void ReloadSourceOptions()
    {
        var selectedId = (SourceFilterBox.SelectedItem as SourceFilter)?.Id
            ?? "all";
        var options = new[]
            {
                new SourceFilter(
                    "all",
                    LocalizationService.Get("AllSources")),
            }
            .Concat(_allConversations
                .Select(value => value.SourceAgent)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .Select(value => new SourceFilter(value, value)))
            .ToArray();
        _loadingSources = true;
        SourceFilterBox.ItemsSource = options;
        SourceFilterBox.SelectedItem = options.FirstOrDefault(
            value => value.Id == selectedId) ?? options[0];
        _loadingSources = false;
    }

    private void ReloadConversationSections()
    {
        var source = (SourceFilterBox.SelectedItem as SourceFilter)?.Id ?? "all";
        var filtered = source == "all"
            ? _allConversations
            : _allConversations
                .Where(value => value.SourceAgent.Equals(
                    source,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
        CurrentSourceCount.Text = filtered.Count.ToString();
        var recent = filtered.Take(8).ToArray();
        RecentConversationList.ItemsSource = recent;
        NoRecentText.Visibility = recent.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        var projects = _machineGrouping
            .Build(filtered, _settings)
            .SelectMany(group => group.Projects)
            .Select(project => new ProjectRow(project))
            .OrderByDescending(value => value.Latest.UpdatedAt)
            .Take(8)
            .ToArray();
        ProjectList.ItemsSource = projects;
        ManageMachineGroupsButton.IsEnabled = projects.Length > 0;
        NoProjectsText.Visibility = projects.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void SourceFilterBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs args)
    {
        if (!_loadingSources) ReloadConversationSections();
    }

    private void RecentConversationList_ItemClick(
        object sender,
        ItemClickEventArgs args)
    {
        if (_window is not null
            && args.ClickedItem is ConversationSummary conversation)
        {
            OpenConversation(conversation);
        }
    }

    private void ProjectList_ItemClick(
        object sender,
        ItemClickEventArgs args)
    {
        if (args.ClickedItem is ProjectRow project)
        {
            OpenConversation(project.Latest);
        }
    }

    private void OpenConversation(ConversationSummary conversation)
    {
        if (_window is null) return;
        Frame.Navigate(
            typeof(ConversationPage),
            new ConversationNavigation(_window, conversation));
    }

    private static async Task<int> CountAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        string query)
    {
        var command = connection.CreateCommand();
        command.CommandText = query;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private async void Refresh_Click(object sender, RoutedEventArgs args)
    {
        await ReloadAsync();
        ShowStatus(
            LocalizationService.Get("RefreshCompletedTitle"),
            LocalizationService.Get("RefreshCompletedBody"));
    }

    private void History_Click(object sender, RoutedEventArgs args) =>
        Frame.Navigate(typeof(HistoryPage), _window);

    private async void ManageMachineGroups_Click(
        object sender,
        RoutedEventArgs args)
    {
        if (_window is null) return;
        var groups = _machineGrouping.Build(_allConversations, _settings);
        if (groups.Count == 0)
        {
            ShowStatus(
                LocalizationService.Get("ComputerGroupsTitle"),
                LocalizationService.Get("NoProjectsToManage"));
            return;
        }

        var knownMachineIds = groups
            .Select(group => group.Id)
            .Concat(groups.SelectMany(group => group.Projects)
                .Select(project =>
                    MachineGroupingService.DetectMachineId(project.Path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var choices = knownMachineIds
            .Select(id => new MachineChoice(
                id,
                _machineGrouping.LabelFor(id, _settings)))
            .OrderBy(choice => choice.Label, StringComparer.CurrentCulture)
            .ToArray();
        var nameFields = new Dictionary<string, TextBox>(
            StringComparer.OrdinalIgnoreCase);
        var mergeFields = new Dictionary<string, ComboBox>(
            StringComparer.OrdinalIgnoreCase);
        var projectFields = new Dictionary<string, ComboBox>(
            StringComparer.OrdinalIgnoreCase);
        var content = new StackPanel { Spacing = 16 };
        content.Children.Add(new TextBlock
        {
            Text = LocalizationService.Get("ComputerGroupsDescription"),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.65,
        });

        foreach (var group in groups)
        {
            var section = new StackPanel { Spacing = 10 };
            var nameField = new TextBox
            {
                Header = LocalizationService.Get("ComputerName"),
                Text = group.Label,
                PlaceholderText = group.Label,
            };
            nameFields[group.Id] = nameField;
            section.Children.Add(nameField);
            if (choices.Length > 1)
            {
                var mergeChoices = new[]
                    {
                        new MachineChoice(
                            "",
                            LocalizationService.Get("DoNotMerge")),
                    }
                    .Concat(choices.Where(choice => !choice.Id.Equals(
                        group.Id,
                        StringComparison.OrdinalIgnoreCase)))
                    .ToArray();
                var mergeField = new ComboBox
                {
                    Header = LocalizationService.Get("MergeComputer"),
                    ItemsSource = mergeChoices,
                    DisplayMemberPath = nameof(MachineChoice.Label),
                    SelectedIndex = 0,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                };
                mergeFields[group.Id] = mergeField;
                section.Children.Add(mergeField);
            }

            foreach (var project in group.Projects)
            {
                var row = new StackPanel { Spacing = 5 };
                row.Children.Add(new TextBlock
                {
                    Text = LocalizationService.Format(
                        "ProjectConversationCount",
                        project.Label,
                        project.Count),
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
                row.Children.Add(new TextBlock
                {
                    Text = project.Path,
                    Opacity = 0.55,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
                var target = new ComboBox
                {
                    Header = LocalizationService.Get("AssignedComputer"),
                    ItemsSource = choices,
                    DisplayMemberPath = nameof(MachineChoice.Label),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                };
                target.SelectedItem = choices.First(
                    choice => choice.Id.Equals(
                        project.MachineId,
                        StringComparison.OrdinalIgnoreCase));
                projectFields[project.Path] = target;
                row.Children.Add(target);
                section.Children.Add(row);
            }

            content.Children.Add(section);
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = LocalizationService.Get("ManageComputerGroups"),
            Content = new ScrollViewer
            {
                Content = content,
                MaxHeight = 520,
                MinWidth = 620,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            },
            PrimaryButtonText = LocalizationService.Get("Save"),
            SecondaryButtonText =
                LocalizationService.Get("RestoreAutomaticGrouping"),
            CloseButtonText = LocalizationService.Get("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.None) return;

        if (result == ContentDialogResult.Secondary)
        {
            _settings.MachineGroupOverrides.Clear();
            await _window.Settings.SaveAsync(_settings);
            await ReloadAsync();
            ShowStatus(
                LocalizationService.Get("ComputerGroupsTitle"),
                LocalizationService.Get("AutomaticGroupingRestored"));
            return;
        }

        foreach (var (id, field) in nameFields)
        {
            var name = field.Text.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                _settings.MachineGroupNames.Remove(id);
            }
            else
            {
                _settings.MachineGroupNames[id] = name;
            }
        }
        foreach (var (path, field) in projectFields)
        {
            if (field.SelectedItem is not MachineChoice choice) continue;
            var automaticId = MachineGroupingService.DetectMachineId(path);
            if (choice.Id.Equals(
                    automaticId,
                    StringComparison.OrdinalIgnoreCase))
            {
                _settings.MachineGroupOverrides.Remove(path);
            }
            else
            {
                _settings.MachineGroupOverrides[path] = choice.Id;
            }
        }
        foreach (var group in groups)
        {
            if (!mergeFields.TryGetValue(group.Id, out var field)
                || field.SelectedItem is not MachineChoice choice
                || string.IsNullOrWhiteSpace(choice.Id))
            {
                continue;
            }
            foreach (var project in group.Projects)
            {
                _settings.MachineGroupOverrides[project.Path] = choice.Id;
            }
        }
        await _window.Settings.SaveAsync(_settings);
        await ReloadAsync();
        ShowStatus(
            LocalizationService.Get("ComputerGroupsTitle"),
            LocalizationService.Get("ComputerGroupsSaved"));
    }

    private async void Sync_Click(object sender, RoutedEventArgs args)
    {
        if (_window is null) return;
        var settings = await _window.Settings.LoadAsync();
        if (settings.Sync.Provider == "local"
            && !string.IsNullOrWhiteSpace(settings.Sync.SyncFolder))
        {
            try
            {
                var localResult = await new LocalFolderSyncService(_window.Conversations)
                    .SyncAsync(settings.Sync.SyncFolder);
                ShowStatus(
                    LocalizationService.Get("SyncCompletedTitle"),
                    LocalizationService.Format(
                        "LocalSyncCompleted",
                        localResult.Uploaded,
                        localResult.Downloaded,
                        localResult.Skipped));
                await ReloadAsync();
            }
            catch (Exception exception)
            {
                AIMemory.Windows.Services.FeedbackPresenter.Show(
                    StatusBar,
                    exception.Message,
                    InfoBarSeverity.Error,
                    LocalizationService.Get("SyncFailedTitle"));
            }
            return;
        }
        if (settings.Sync.Provider != "webdav"
            || string.IsNullOrWhiteSpace(settings.Sync.WebdavHost))
        {
            ShowStatus(
                LocalizationService.Get("SyncTitle"),
                LocalizationService.Get("ConfigureWebDavFirst"));
            return;
        }
        try
        {
            var credentials = new AIMemory.Windows.Services.CredentialService().Load();
            var result = await new WebDavService(_window.Conversations).SyncAsync(
                WebDavService.BuildCollectionUri(settings.Sync),
                credentials?.Username ?? settings.Sync.Username,
                credentials?.Password);
            ShowStatus(
                LocalizationService.Get("SyncCompletedTitle"),
                LocalizationService.Format(
                    "SyncCompleted",
                    result.Uploaded,
                    result.Downloaded,
                    result.Skipped));
            await ReloadAsync();
        }
        catch (Exception exception)
        {
            AIMemory.Windows.Services.FeedbackPresenter.Show(
                StatusBar,
                exception.Message,
                InfoBarSeverity.Error,
                LocalizationService.Get("SyncFailedTitle"));
        }
    }

    private void ShowStatus(string title, string message)
        => AIMemory.Windows.Services.FeedbackPresenter.Show(
            StatusBar,
            message,
            InfoBarSeverity.Success,
            title);
}

public sealed record SourceFilter(string Id, string Label);
public sealed record MachineChoice(string Id, string Label);

public sealed class ProjectRow
{
    public ProjectRow(MachineProjectGroup project)
    {
        ProjectPath = project.Path;
        MachineLabel = project.MachineLabel;
        Latest = project.Latest;
        Count = project.Count;
    }

    public string ProjectPath { get; }
    public string MachineLabel { get; }
    public ConversationSummary Latest { get; }
    public int Count { get; }
    public string DisplayName => string.IsNullOrWhiteSpace(ProjectPath)
        ? LocalizationService.Get("UnknownProject")
        : ProjectPath.TrimEnd('\\', '/').Split('\\', '/').Last();
    public string LatestTitle => string.IsNullOrWhiteSpace(Latest.Summary)
        ? LocalizationService.Get("UntitledConversation")
        : Latest.Summary;
    public string CountLabel =>
        LocalizationService.Format("ConversationCount", Count);
}
