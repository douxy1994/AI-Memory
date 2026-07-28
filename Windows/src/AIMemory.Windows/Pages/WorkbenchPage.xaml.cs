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

        var projects = filtered
            .GroupBy(value => string.IsNullOrWhiteSpace(value.ProjectPath)
                ? value.RepoId
                : value.ProjectPath)
            .Select(group => new ProjectRow(group.Key, group.ToArray()))
            .OrderByDescending(value => value.Latest.UpdatedAt)
            .Take(8)
            .ToArray();
        ProjectList.ItemsSource = projects;
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

public sealed class ProjectRow
{
    public ProjectRow(
        string projectPath,
        IReadOnlyList<ConversationSummary> conversations)
    {
        ProjectPath = projectPath;
        Latest = conversations.OrderByDescending(
            value => value.UpdatedAt).First();
        Count = conversations.Count;
    }

    public string ProjectPath { get; }
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
