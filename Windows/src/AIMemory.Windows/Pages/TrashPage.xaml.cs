using AIMemory.Core.Models;
using AIMemory.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace AIMemory.Windows.Pages;

public sealed partial class TrashPage : Page
{
    private MainWindow? _window;
    private TrashService? _trash;
    private bool _loading;

    public TrashPage() => InitializeComponent();

    protected override async void OnNavigatedTo(NavigationEventArgs args)
    {
        _window = (MainWindow)args.Parameter;
        _trash = new TrashService(_window.Database);
        _loading = true;
        var settings = await _window.Settings.LoadAsync();
        TrashRetentionBox.Value = settings.TrashRetentionDays;
        _loading = false;
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        if (_trash is null) return;
        var records = await _trash.ListAsync();
        TrashList.ItemsSource = records;
        EmptyText.Visibility = records.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        EmptyTrashButton.IsEnabled = records.Count > 0;
        TrashSummaryText.Text =
            $"已删除对话的可恢复记录（{records.Count} 条，保留 {TrashRetentionBox.Value:0} 天）。";
    }

    private async void Refresh_Click(object sender, RoutedEventArgs args) =>
        await ReloadAsync();

    private async void TrashRetentionBox_ValueChanged(
        NumberBox sender,
        NumberBoxValueChangedEventArgs args)
    {
        if (_loading || _window is null || double.IsNaN(args.NewValue)) return;
        var settings = await _window.Settings.LoadAsync();
        settings.TrashRetentionDays =
            Math.Clamp((int)Math.Round(args.NewValue), 1, 365);
        await _window.Settings.SaveAsync(settings);
        TrashSummaryText.Text =
            $"已删除对话的可恢复记录（保留 {settings.TrashRetentionDays} 天）。";
        Show("回收站保留天数已更新。", InfoBarSeverity.Success);
    }

    private async void EmptyTrash_Click(object sender, RoutedEventArgs args)
    {
        if (_trash is null) return;
        var records = await _trash.ListAsync();
        if (records.Count == 0) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "清空回收站？",
            Content = $"将永久删除全部 {records.Count} 条回收站记录，此操作无法撤销。",
            PrimaryButtonText = "永久清空",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            var count = await _trash.EmptyAsync();
            await ReloadAsync();
            Show($"已永久删除 {count} 条回收站记录。",
                InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            Show($"清空回收站失败：{exception.Message}",
                InfoBarSeverity.Error);
        }
    }

    private async void Restore_Click(object sender, RoutedEventArgs args)
    {
        if (_trash is null
            || sender is not AppBarButton { Tag: TrashRecord record })
        {
            return;
        }
        try
        {
            await _trash.RestoreAsync(record);
            await ReloadAsync();
            Show("对话已恢复。", InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            Show($"恢复失败：{exception.Message}", InfoBarSeverity.Error);
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs args)
    {
        if (_trash is null
            || sender is not AppBarButton { Tag: TrashRecord record })
        {
            return;
        }
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "永久删除？",
            Content = "此操作无法撤销。",
            PrimaryButtonText = "永久删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            await _trash.DeleteAsync(record);
            await ReloadAsync();
            Show("回收站记录已永久删除。", InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            Show($"永久删除失败：{exception.Message}",
                InfoBarSeverity.Error);
        }
    }

    private void Show(string message, InfoBarSeverity severity)
        => AIMemory.Windows.Services.FeedbackPresenter.Show(
            Feedback,
            message,
            severity);
}
