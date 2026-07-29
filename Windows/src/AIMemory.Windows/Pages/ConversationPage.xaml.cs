using AIMemory.Core.Models;
using AIMemory.Core.Services;
using AIMemory.Windows.Services;
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
            ? LocalizationService.Get("UntitledConversation")
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
            ? LocalizationService.Get("NotProvided")
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
            ? LocalizationService.Get("RemoveFavorite")
            : LocalizationService.Get("Favorite");
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
            Show(
                LocalizationService.Get(
                    enabled
                        ? "ConversationFavorited"
                        : "ConversationUnfavorited"),
                InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            Show(
                LocalizationService.Format(
                    "FavoriteUpdateFailed",
                    exception.Message),
                InfoBarSeverity.Error);
        }
    }

    private void CopyPath_Click(object sender, RoutedEventArgs args)
    {
        if (_detail is null) return;
        CopyText(_detail.ProjectDir);
        Show(
            LocalizationService.Get("ProjectPathCopied"),
            InfoBarSeverity.Success);
    }

    private void CopyResume_Click(object sender, RoutedEventArgs args)
    {
        if (string.IsNullOrWhiteSpace(_detail?.ResumeCommand)) return;
        CopyText(_detail.ResumeCommand);
        Show(
            LocalizationService.Get("ResumeCommandCopied"),
            InfoBarSeverity.Success);
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
                LocalizationService.Get("FullConversationCopy"),
                LocalizationService.Get("SummaryMigration"),
            },
            SelectedIndex = 0,
        };
        var target = new ComboBox
        {
            Header = LocalizationService.Get("TargetAgent"),
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
                        LocalizationService.Get("CopyKeepSource"),
                        LocalizationService.Get("MoveAfterVerification"),
                    }
                    : new[]
                    {
                        LocalizationService.Get("CopySourceCannotMove"),
                    },
            SelectedIndex = 0,
        };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = LocalizationService.Get("FullMigrationDescription"),
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
            Title = LocalizationService.Get("MigrateConversation"),
            Content = content,
            PrimaryButtonText = LocalizationService.Get("Continue"),
            CloseButtonText = LocalizationService.Get("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        if (kind.SelectedIndex == 1)
        {
            CopyText(ConversationMigrationService.ContinuationCard(_detail));
            Show(
                LocalizationService.Get("ContinuationCardCopied"),
                InfoBarSeverity.Success);
            return;
        }
        if (target.SelectedItem is not AgentIntegrationStatus selected)
        {
            Show(
                targets.Length == 0
                    ? LocalizationService.Get("NoSafeWritableAgent")
                    : LocalizationService.Get("SelectTargetAgent"),
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
                    LocalizationService.Format(
                        "MigrationMoveSucceeded",
                        selected.Label),
                    InfoBarSeverity.Success);
                _context.Window.NavigateTo("trash");
                return;
            }
            Show(
                LocalizationService.Format(
                    "MigrationSucceeded",
                    selected.Label,
                    result.NewId[..8]),
                InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            Show(
                LocalizationService.Format(
                    "MigrationFailed",
                    exception.Message),
                InfoBarSeverity.Error);
        }
    }

    private async void Trash_Click(object sender, RoutedEventArgs args)
    {
        if (_context is null) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = LocalizationService.Get("MoveToTrashQuestion"),
            Content = LocalizationService.Get("MoveToTrashDescription"),
            PrimaryButtonText = LocalizationService.Get("MoveToTrash"),
            CloseButtonText = LocalizationService.Get("Cancel"),
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
            Show(LocalizationService.Format(
                    "MoveToTrashFailed",
                    exception.Message),
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
            Show(
                LocalizationService.Get("CheckpointCreated"),
                InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            Show(
                LocalizationService.Format(
                    "CheckpointCreationFailed",
                    exception.Message),
                InfoBarSeverity.Error);
        }
    }

    private async void CreateHandoff_Click(object sender, RoutedEventArgs args)
    {
        if (_context is null
            || TargetAgentBox.SelectedItem is not AgentIntegrationStatus target)
        {
            Show(
                LocalizationService.Get("SelectTargetAgent"),
                InfoBarSeverity.Warning);
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
            Show(
                LocalizationService.Format(
                    "HandoffCreated",
                    target.Label),
                InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            Show(
                LocalizationService.Format(
                    "HandoffCreationFailed",
                    exception.Message),
                InfoBarSeverity.Error);
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
