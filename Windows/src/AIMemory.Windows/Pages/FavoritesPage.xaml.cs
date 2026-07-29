using AIMemory.Core.Models;
using AIMemory.Core.Services;
using AIMemory.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;

namespace AIMemory.Windows.Pages;

public sealed partial class FavoritesPage : Page
{
    private MainWindow? _window;
    private FavoriteService? _favorites;

    public FavoritesPage() => InitializeComponent();

    protected override async void OnNavigatedTo(NavigationEventArgs args)
    {
        _window = (MainWindow)args.Parameter;
        _favorites = new FavoriteService(_window.Settings);
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        if (_window is null) return;
        var settings = await _window.Settings.LoadAsync();
        var rows = settings.FavoriteConversations.Values
            .OrderByDescending(value => value.Pinned)
            .ThenByDescending(value => value.UpdatedAt)
            .Select(value => new FavoriteRow(value))
            .ToArray();
        FavoritesList.ItemsSource = rows;
        EmptyText.Visibility = rows.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async void TogglePin_Click(object sender, RoutedEventArgs args)
    {
        if (_favorites is null
            || sender is not AppBarButton { Tag: FavoriteRow row })
        {
            return;
        }
        try
        {
            await _favorites.UpdateAsync(
                row.Value.SourceAgent,
                row.Value.Id,
                row.Note,
                ParseTags(row.TagsText),
                !row.Value.Pinned);
            await ReloadAsync();
            Show(
                LocalizationService.Get(
                    row.Value.Pinned
                        ? "FavoriteUnpinned"
                        : "FavoritePinned"),
                InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            Show(LocalizationService.Format(
                    "FavoritePinUpdateFailed",
                    exception.Message),
                InfoBarSeverity.Error);
        }
    }

    private async void SaveMetadata_Click(object sender, RoutedEventArgs args)
    {
        if (_favorites is null
            || sender is not AppBarButton { Tag: FavoriteRow row })
        {
            return;
        }
        try
        {
            await _favorites.UpdateAsync(
                row.Value.SourceAgent,
                row.Value.Id,
                row.Note,
                ParseTags(row.TagsText));
            await ReloadAsync();
            Show(
                LocalizationService.Get("FavoriteMetadataSaved"),
                InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            Show(
                LocalizationService.Format(
                    "FavoriteSaveFailed",
                    exception.Message),
                InfoBarSeverity.Error);
        }
    }

    private async void OpenConversation_Click(object sender, RoutedEventArgs args)
    {
        if (_window is null
            || sender is not AppBarButton { Tag: FavoriteRow row })
        {
            return;
        }
        var conversation = (await _window.Conversations.ListAsync(
                sourceAgent: row.Value.SourceAgent,
                limit: 5_000))
            .FirstOrDefault(value => value.Id == row.Value.Id);
        if (conversation is null)
        {
            Show(
                LocalizationService.Get("FavoriteSourceUnavailable"),
                InfoBarSeverity.Warning);
            return;
        }
        Frame.Navigate(
            typeof(ConversationPage),
            new ConversationNavigation(_window, conversation));
    }

    private void CopyCard_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not AppBarButton { Tag: FavoriteRow row }) return;
        var current = row.Value with
        {
            Note = row.Note.Trim(),
            Tags = ParseTags(row.TagsText),
        };
        var package = new DataPackage();
        package.SetText(FavoriteService.ContinuationCard(current));
        Clipboard.SetContent(package);
        Clipboard.Flush();
        Show(
            LocalizationService.Get("FavoriteCardCopied"),
            InfoBarSeverity.Success);
    }

    private async void RemoveFavorite_Click(object sender, RoutedEventArgs args)
    {
        if (_favorites is null
            || sender is not AppBarButton { Tag: FavoriteRow row })
        {
            return;
        }
        await _favorites.RemoveAsync(row.Value.SourceAgent, row.Value.Id);
        await ReloadAsync();
        Show(
            LocalizationService.Get("ConversationUnfavorited"),
            InfoBarSeverity.Success);
    }

    private static string[] ParseTags(string value) =>
        value.Split(
                [',', '，'],
                StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private void Show(string message, InfoBarSeverity severity)
        => AIMemory.Windows.Services.FeedbackPresenter.Show(
            Feedback,
            message,
            severity);
}

public sealed class FavoriteRow
{
    public FavoriteRow(FavoriteConversationSnapshot value)
    {
        Value = value;
        Note = value.Note;
        TagsText = value.Tags is { Count: > 0 }
            ? string.Join(", ", value.Tags)
            : "";
    }

    public FavoriteConversationSnapshot Value { get; }
    public string Title => Value.Title;
    public string SourceAndProject =>
        $"{Value.SourceAgent} · {(string.IsNullOrWhiteSpace(Value.ProjectPath) ? LocalizationService.Get("UnknownProject") : Value.ProjectPath)}";
    public string PinLabel =>
        Value.Pinned ? LocalizationService.Get("Pinned") : "";
    public string PinActionLabel => LocalizationService.Get(
        Value.Pinned ? "Unpin" : "Pin");
    public string Note { get; set; }
    public string TagsText { get; set; }
}
