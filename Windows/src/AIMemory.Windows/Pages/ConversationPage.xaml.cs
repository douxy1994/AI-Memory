using AIMemory.Core.Models;
using AIMemory.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;

namespace AIMemory.Windows.Pages;

public sealed partial class ConversationPage : Page
{
    private ConversationNavigation? _context;
    private WebDavConversationDetail? _detail;
    private FavoriteService? _favorites;

    public ConversationPage() => InitializeComponent();

    protected override async void OnNavigatedTo(NavigationEventArgs args)
    {
        var context = (ConversationNavigation)args.Parameter;
        _context = context;
        _favorites = new FavoriteService(context.Window.Settings);
        TitleText.Text = string.IsNullOrWhiteSpace(context.Conversation.Summary)
            ? "未命名对话"
            : context.Conversation.Summary;
        _detail = await context.Window.Conversations.ExportAsync(
            context.Conversation.Id);
        MetadataText.Text =
            $"{context.Conversation.SourceAgent} · {_detail.ProjectDir} · {_detail.UpdatedAt}";
        MessageList.ItemsSource = _detail.Messages;
        FileChangeList.ItemsSource = _detail.FileChanges;
        MessageCountText.Text = _detail.Messages.Count.ToString();
        FileCountText.Text = _detail.FileChanges.Count.ToString();
        ToolCallCountText.Text = _detail.Messages
            .Sum(value => value.ToolCalls.Count)
            .ToString();
        NoFileChangesText.Visibility = _detail.FileChanges.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        ResumeCommandBox.Text = _detail.ResumeCommand ?? "";
        ResumePanel.Visibility = string.IsNullOrWhiteSpace(_detail.ResumeCommand)
            ? Visibility.Collapsed
            : Visibility.Visible;
        StoragePathText.Text = string.IsNullOrWhiteSpace(_detail.StoragePath)
            ? "未提供"
            : _detail.StoragePath;
        await ReloadFavoriteStateAsync();
        var detected = new AgentCatalog().Detect()
            .Where(value => value.IsDetected)
            .ToArray();
        TargetAgentBox.ItemsSource = detected.Length > 0
            ? detected
            : new AgentCatalog().Detect();
        TargetAgentBox.SelectedIndex = 0;
    }

    private async Task ReloadFavoriteStateAsync()
    {
        if (_context is null || _favorites is null) return;
        FavoriteButton.Label = await _favorites.IsFavoriteAsync(
            _context.Conversation.SourceAgent,
            _context.Conversation.Id)
            ? "取消收藏"
            : "收藏";
    }

    private void Back_Click(object sender, RoutedEventArgs args)
    {
        if (Frame.CanGoBack) Frame.GoBack();
    }

    private async void Favorite_Click(object sender, RoutedEventArgs args)
    {
        if (_context is null || _detail is null || _favorites is null) return;
        try
        {
            var enabled = await _favorites.ToggleAsync(
                _context.Conversation,
                _detail.ProjectDir);
            await ReloadFavoriteStateAsync();
            Show(enabled ? "对话已收藏。" : "已取消收藏。",
                InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            Show($"更新收藏失败：{exception.Message}", InfoBarSeverity.Error);
        }
    }

    private void CopyPath_Click(object sender, RoutedEventArgs args)
    {
        if (_detail is null) return;
        CopyText(_detail.ProjectDir);
        Show("项目路径已复制。", InfoBarSeverity.Success);
    }

    private void CopyResume_Click(object sender, RoutedEventArgs args)
    {
        if (string.IsNullOrWhiteSpace(_detail?.ResumeCommand)) return;
        CopyText(_detail.ResumeCommand);
        Show("恢复命令已复制。", InfoBarSeverity.Success);
    }

    private async void Trash_Click(object sender, RoutedEventArgs args)
    {
        if (_context is null) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "移到回收站？",
            Content = "对话会从 AI Memory 数据库移除，并保留可恢复副本。",
            PrimaryButtonText = "移到回收站",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            var settings = await _context.Window.Settings.LoadAsync();
            await new TrashService(_context.Window.Database).TrashAsync(
                _context.Conversation,
                settings.TrashRetentionDays);
            Frame.Navigate(typeof(HistoryPage), _context.Window);
        }
        catch (Exception exception)
        {
            Show($"移到回收站失败：{exception.Message}",
                InfoBarSeverity.Error);
        }
    }

    private async void CreateCheckpoint_Click(object sender, RoutedEventArgs args)
    {
        if (_context is null) return;
        try
        {
            var messageCount = (await _context.Window.Conversations.ReadMessagesAsync(
                _context.Conversation.Id)).Count;
            await new RecoveryService(_context.Window.Database)
                .CreateCheckpointAsync(_context.Conversation, messageCount);
            Show("检查点已创建。", InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            Show($"创建检查点失败：{exception.Message}", InfoBarSeverity.Error);
        }
    }

    private async void CreateHandoff_Click(object sender, RoutedEventArgs args)
    {
        if (_context is null
            || TargetAgentBox.SelectedItem is not AgentIntegrationStatus target)
        {
            Show("请选择目标 Agent。", InfoBarSeverity.Warning);
            return;
        }
        try
        {
            var messages = await _context.Window.Conversations.ReadMessagesAsync(
                _context.Conversation.Id);
            var service = new RecoveryService(_context.Window.Database);
            var checkpoint = await service.CreateCheckpointAsync(
                _context.Conversation, messages.Count);
            await service.CreateHandoffAsync(checkpoint, target.Id);
            Show($"已创建发往 {target.Label} 的交接包。", InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            Show($"创建交接失败：{exception.Message}", InfoBarSeverity.Error);
        }
    }

    private void Show(string message, InfoBarSeverity severity)
        => AIMemory.Windows.Services.FeedbackPresenter.Show(
            Feedback,
            message,
            severity);

    private static void CopyText(string value)
    {
        var package = new DataPackage();
        package.SetText(value);
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }
}
