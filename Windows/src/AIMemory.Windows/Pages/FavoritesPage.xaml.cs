using AIMemory.Core.Models;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace AIMemory.Windows.Pages;

public sealed partial class FavoritesPage : Page
{
    private MainWindow? _window;

    public FavoritesPage() => InitializeComponent();

    protected override async void OnNavigatedTo(NavigationEventArgs args)
    {
        _window = (MainWindow)args.Parameter;
        var settings = await _window.Settings.LoadAsync();
        FavoritesList.ItemsSource = settings.FavoriteConversations.Values
            .OrderByDescending(value => value.Pinned)
            .ThenByDescending(value => value.UpdatedAt)
            .ToArray();
    }

    private async void FavoritesList_ItemClick(
        object sender,
        ItemClickEventArgs args)
    {
        if (_window is null
            || args.ClickedItem is not FavoriteConversationSnapshot favorite)
        {
            return;
        }
        var conversation = (await _window.Conversations.ListAsync(
                sourceAgent: favorite.SourceAgent,
                limit: 5_000))
            .FirstOrDefault(value => value.Id == favorite.Id);
        if (conversation is not null)
        {
            Frame.Navigate(
                typeof(ConversationPage),
                new ConversationNavigation(_window, conversation));
        }
    }
}
