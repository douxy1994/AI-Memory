using AIMemory.Core.Models;
using AIMemory.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace AIMemory.Windows.Pages;

public sealed partial class TrashPage : Page
{
    private TrashService? _trash;

    public TrashPage() => InitializeComponent();

    protected override async void OnNavigatedTo(NavigationEventArgs args)
    {
        var window = (MainWindow)args.Parameter;
        _trash = new TrashService(window.Database);
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        if (_trash is not null)
        {
            TrashList.ItemsSource = await _trash.ListAsync();
        }
    }

    private async void Restore_Click(object sender, RoutedEventArgs args)
    {
        if (_trash is null || sender is not Button { Tag: TrashRecord record }) return;
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
        if (_trash is null || sender is not Button { Tag: TrashRecord record }) return;
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
        _trash.Delete(record);
        await ReloadAsync();
        Show("回收站记录已永久删除。", InfoBarSeverity.Success);
    }

    private void Show(string message, InfoBarSeverity severity)
    {
        Feedback.Message = message;
        Feedback.Severity = severity;
        Feedback.IsOpen = true;
    }
}
