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
        var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM approved_memories WHERE status='approved';";
        MemoryCount.Text = Convert.ToInt32(await command.ExecuteScalarAsync()).ToString();
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
                StatusBar.Title = "同步失败";
                StatusBar.Message = exception.Message;
                StatusBar.Severity = InfoBarSeverity.Error;
                StatusBar.IsOpen = true;
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
            StatusBar.Title = "同步失败";
            StatusBar.Message = exception.Message;
            StatusBar.Severity = InfoBarSeverity.Error;
            StatusBar.IsOpen = true;
        }
    }

    private void ShowStatus(string title, string message)
    {
        StatusBar.Title = title;
        StatusBar.Message = message;
        StatusBar.Severity = InfoBarSeverity.Success;
        StatusBar.IsOpen = true;
    }
}
