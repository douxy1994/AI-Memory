using AIMemory.Core.Models;
using AIMemory.Core.Services;
using AIMemory.Windows.Services;
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
        TrashSummaryText.Text = LocalizationService.Format(
            "TrashSummaryWithCount",
            records.Count,
            TrashRetentionBox.Value);
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
        TrashSummaryText.Text = LocalizationService.Format(
            "TrashSummaryRetention",
            settings.TrashRetentionDays);
        Show(
            LocalizationService.Get("TrashRetentionUpdated"),
            InfoBarSeverity.Success);
    }

    private async void EmptyTrash_Click(object sender, RoutedEventArgs args)
    {
        if (_trash is null) return;
        var records = await _trash.ListAsync();
        if (records.Count == 0) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = LocalizationService.Get("EmptyTrashQuestion"),
            Content = LocalizationService.Format(
                "EmptyTrashDescription",
                records.Count),
            PrimaryButtonText = LocalizationService.Get("EmptyPermanently"),
            CloseButtonText = LocalizationService.Get("Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            var count = await _trash.EmptyAsync();
            await ReloadAsync();
            Show(
                LocalizationService.Format(
                    "TrashRecordsDeleted",
                    count),
                InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            Show(LocalizationService.Format(
                    "EmptyTrashFailed",
                    exception.Message),
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
            Show(
                LocalizationService.Get("ConversationRestored"),
                InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            Show(
                LocalizationService.Format(
                    "RestoreFailed",
                    exception.Message),
                InfoBarSeverity.Error);
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
            Title = LocalizationService.Get("DeletePermanentlyQuestion"),
            Content = LocalizationService.Get("CannotUndo"),
            PrimaryButtonText =
                LocalizationService.Get("DeletePermanently"),
            CloseButtonText = LocalizationService.Get("Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            await _trash.DeleteAsync(record);
            await ReloadAsync();
            Show(
                LocalizationService.Get("TrashRecordDeleted"),
                InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            Show(LocalizationService.Format(
                    "DeletePermanentlyFailed",
                    exception.Message),
                InfoBarSeverity.Error);
        }
    }

    private void Show(string message, InfoBarSeverity severity)
        => AIMemory.Windows.Services.FeedbackPresenter.Show(
            Feedback,
            message,
            severity);
}
