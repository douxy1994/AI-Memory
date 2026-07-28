using AIMemory.Core.Models;
using AIMemory.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace AIMemory.Windows.Pages;

public sealed partial class WorkbenchPage : Page
{
    private MainWindow? _window;

    public WorkbenchPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs args)
    {
        _window = (MainWindow)args.Parameter;
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        if (_window is null) return;
        ConversationCount.Text = (await _window.Conversations.CountAsync()).ToString();
        var agents = new AgentCatalog().Detect();
        var detected = agents.Where(value => value.IsDetected).ToArray();
        DetectedAgentCount.Text = detected.Length.ToString();
        AgentList.ItemsSource = detected;
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
