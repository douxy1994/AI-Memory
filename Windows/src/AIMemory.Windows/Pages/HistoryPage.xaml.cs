using AIMemory.Core.Models;
using AIMemory.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace AIMemory.Windows.Pages;

public sealed partial class HistoryPage : Page
{
    private MainWindow? _window;
    private CancellationTokenSource? _searchCancellation;

    public HistoryPage() => InitializeComponent();

    protected override async void OnNavigatedTo(NavigationEventArgs args)
    {
        _window = (MainWindow)args.Parameter;
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        if (_window is null) return;
        _searchCancellation?.Cancel();
        _searchCancellation = new CancellationTokenSource();
        var items = await _window.Conversations.ListAsync(
            search: string.IsNullOrWhiteSpace(SearchBox.Text) ? null : SearchBox.Text,
            cancellationToken: _searchCancellation.Token);
        ConversationList.ItemsSource = items;
        EmptyText.Visibility = items.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs args) =>
        await ReloadAsync();

    private async void SearchBox_TextChanged(object sender, TextChangedEventArgs args)
    {
        await Task.Delay(180);
        await ReloadAsync();
    }

    private void ConversationList_ItemClick(object sender, ItemClickEventArgs args)
    {
        if (_window is not null && args.ClickedItem is ConversationSummary conversation)
        {
            Frame.Navigate(
                typeof(ConversationPage),
                new ConversationNavigation(_window, conversation));
        }
    }

    private async void TrashSelected_Click(object sender, RoutedEventArgs args)
    {
        if (_window is null
            || ConversationList.SelectedItem is not ConversationSummary conversation)
        {
            return;
        }
        var settings = await _window.Settings.LoadAsync();
        await new TrashService(_window.Database).TrashAsync(
            conversation,
            settings.TrashRetentionDays);
        await ReloadAsync();
    }
}

public sealed record ConversationNavigation(
    MainWindow Window,
    ConversationSummary Conversation);
