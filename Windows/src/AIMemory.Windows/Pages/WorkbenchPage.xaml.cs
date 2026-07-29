using AIMemory.Core.Models;
using AIMemory.Core.Services;
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
        AgentList.ItemsSource = detected;
        DetectedAgentSummary.Text =
            $"{detected.Length} / {agents.Count} 项已安装；已安装项目优先显示。";
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
                new SourceFilter("all", "全部来源"),
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
        ShowStatus("刷新完成", "已重新读取本地数据库和 Agent 安装状态。");
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
            ShowStatus("电脑分组", "当前没有可管理的项目。");
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
            Text = "重命名电脑，或把项目移动到另一个电脑分组。"
                + "这里只改变 AI Memory 的展示，不修改原始路径。",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.65,
        });

        foreach (var group in groups)
        {
            var section = new StackPanel { Spacing = 10 };
            var nameField = new TextBox
            {
                Header = "电脑名称",
                Text = group.Label,
                PlaceholderText = group.Label,
            };
            nameFields[group.Id] = nameField;
            section.Children.Add(nameField);
            if (choices.Length > 1)
            {
                var mergeChoices = new[]
                    {
                        new MachineChoice("", "不合并"),
                    }
                    .Concat(choices.Where(choice => !choice.Id.Equals(
                        group.Id,
                        StringComparison.OrdinalIgnoreCase)))
                    .ToArray();
                var mergeField = new ComboBox
                {
                    Header = "合并电脑",
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
                    Text = $"{project.Label} · {project.Count} 条",
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
                    Header = "所属电脑",
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
            Title = "管理电脑分组",
            Content = new ScrollViewer
            {
                Content = content,
                MaxHeight = 520,
                MinWidth = 620,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            },
            PrimaryButtonText = "保存",
            SecondaryButtonText = "恢复自动分组",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.None) return;

        if (result == ContentDialogResult.Secondary)
        {
            _settings.MachineGroupOverrides.Clear();
            await _window.Settings.SaveAsync(_settings);
            await ReloadAsync();
            ShowStatus("电脑分组", "已恢复按项目路径自动分组。");
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
        ShowStatus("电脑分组", "电脑名称和项目分组已保存。");
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
                ShowStatus("同步完成", localResult.Message);
                await ReloadAsync();
            }
            catch (Exception exception)
            {
                AIMemory.Windows.Services.FeedbackPresenter.Show(
                    StatusBar,
                    exception.Message,
                    InfoBarSeverity.Error,
                    "同步失败");
            }
            return;
        }
        if (settings.Sync.Provider != "webdav"
            || string.IsNullOrWhiteSpace(settings.Sync.WebdavHost))
        {
            ShowStatus("同步", "请先在设置中完成 WebDAV 配置。");
            return;
        }
        try
        {
            var credentials = new AIMemory.Windows.Services.CredentialService().Load();
            var result = await new WebDavService(_window.Conversations).SyncAsync(
                WebDavService.BuildCollectionUri(settings.Sync),
                credentials?.Username ?? settings.Sync.Username,
                credentials?.Password);
            ShowStatus("同步完成", result.Message);
            await ReloadAsync();
        }
        catch (Exception exception)
        {
            AIMemory.Windows.Services.FeedbackPresenter.Show(
                StatusBar,
                exception.Message,
                InfoBarSeverity.Error,
                "同步失败");
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
        ? "未知项目"
        : ProjectPath.TrimEnd('\\', '/').Split('\\', '/').Last();
    public string LatestTitle => string.IsNullOrWhiteSpace(Latest.Summary)
        ? "未命名对话"
        : Latest.Summary;
    public string CountLabel => $"{Count} 条";
}
