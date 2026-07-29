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

    private async void Migrate_Click(object sender, RoutedEventArgs args)
    {
        if (_context is null || _detail is null) return;
        var targets = new AgentCatalog().Detect()
            .Where(value =>
                value.IsDetected
                && NativeAgentConversationWriter.WritableTargets.Contains(value.Id)
                && !value.Id.Equals(
                    _context.Conversation.SourceAgent,
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var kind = new RadioButtons
        {
            ItemsSource = new[]
            {
                "完整对话复制",
                "总结式迁移（复制继续卡片）",
            },
            SelectedIndex = 0,
        };
        var target = new ComboBox
        {
            Header = "目标 Agent",
            ItemsSource = targets,
            DisplayMemberPath = "Label",
            SelectedIndex = targets.Length > 0 ? 0 : -1,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var migrationMode = new RadioButtons
        {
            ItemsSource =
                NativeAgentConversationWriter.ArchivableSources.Contains(
                    _context.Conversation.SourceAgent)
                    ? new[]
                    {
                        "复制（保留源）",
                        "移动（验证后将源移入回收站）",
                    }
                    : new[] { "复制（此来源不支持安全移动）" },
            SelectedIndex = 0,
        };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = "完整迁移会写入目标 Agent 的真实本地历史，并在回读验证失败时撤销写入。",
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(kind);
        content.Children.Add(target);
        content.Children.Add(migrationMode);
        kind.SelectionChanged += (_, _) =>
        {
            var visibility = kind.SelectedIndex == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            target.Visibility = visibility;
            migrationMode.Visibility = visibility;
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "迁移对话",
            Content = content,
            PrimaryButtonText = "继续",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        if (kind.SelectedIndex == 1)
        {
            CopyText(ConversationMigrationService.ContinuationCard(_detail));
            Show("继续卡片已复制。", InfoBarSeverity.Success);
            return;
        }
        if (target.SelectedItem is not AgentIntegrationStatus selected)
        {
            Show(
                targets.Length == 0
                    ? "没有检测到可安全写入的目标 Agent。"
                    : "请选择目标 Agent。",
                InfoBarSeverity.Warning);
            return;
        }

        try
        {
            var settings = await _context.Window.Settings.LoadAsync();
            var result = await new ConversationMigrationService(
                    _context.Window.Conversations)
                .MigrateAsync(
                    _context.Conversation.SourceAgent,
                    selected.Id,
                    _context.Conversation.Id,
                    migrationMode.SelectedIndex == 1 ? "cut" : "copy",
                    new TrashService(_context.Window.Database),
                    settings.TrashRetentionDays);
            if (result.CutDeletedSource)
            {
                _context.Window.ShowFeedback(
                    $"移动成功：{selected.Label} · 源对话已进入回收站",
                    InfoBarSeverity.Success);
                _context.Window.NavigateTo("trash");
                return;
            }
            Show(
                $"迁移成功：{selected.Label} · {result.NewId[..8]}…",
                InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            Show($"迁移失败：{exception.Message}", InfoBarSeverity.Error);
        }
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
