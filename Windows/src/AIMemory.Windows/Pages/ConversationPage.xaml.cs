// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
using System.Globalization;
using System.Text.Json;
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
    private CancellationTokenSource? _automaticCapture;
    private CancellationTokenSource? _detailLoad;

    public ConversationPage() => InitializeComponent();

    protected override async void OnNavigatedTo(NavigationEventArgs args)
    {
        if (args.Parameter is not ConversationNavigation context)
        {
            _context = null;
            ShowLoadFailure(
                LocalizationService.Get("ConversationNavigationUnavailable"),
                showFeedback: false);
            return;
        }

        _context = context;
        _favorites = new FavoriteService(context.Window.Settings);
        TitleText.Text = ConversationTitle(context.Conversation);
        MetadataText.Text = "";
        await LoadDetailAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs args)
    {
        CancelDetailLoad();
        _automaticCapture?.Cancel();
        _automaticCapture?.Dispose();
        _automaticCapture = null;
        base.OnNavigatedFrom(args);
    }

    private async Task LoadDetailAsync()
    {
        var context = _context;
        if (context is null)
        {
            ShowLoadFailure(
                LocalizationService.Get("ConversationNavigationUnavailable"),
                showFeedback: false);
            return;
        }

        CancelDetailLoad();
        var request = new CancellationTokenSource();
        _detailLoad = request;
        ShowLoadingState();
        TitleText.Text = ConversationTitle(context.Conversation);
        MetadataText.Text = "";

        try
        {
            var detail = await context.Window.Conversations.ExportAsync(
                context.Conversation.Id,
                request.Token);
            if (request.IsCancellationRequested
                || !ReferenceEquals(_context, context))
            {
                return;
            }

            _detail = detail;
            ApplyDetail(detail);
            ShowLoadedState();

            try
            {
                await ReloadFavoriteStateAsync();
            }
            catch (Exception exception)
            {
                Show(
                    LocalizationService.Format(
                        "FavoriteUpdateFailed",
                        exception.Message),
                    InfoBarSeverity.Warning);
            }

            ConfigureHandoffTargets();
            StartAutomaticCapture();
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
            // A newer navigation or retry superseded this read.
        }
        catch (Exception exception)
        {
            if (!request.IsCancellationRequested
                && ReferenceEquals(_context, context))
            {
                ShowLoadFailure(
                    LocalizationService.Format(
                        "ConversationLoadFailed",
                        exception.Message),
                    showFeedback: true);
            }
        }
        finally
        {
            if (ReferenceEquals(_detailLoad, request))
            {
                _detailLoad = null;
            }
            request.Dispose();
        }
    }

    private void CancelDetailLoad()
    {
        _detailLoad?.Cancel();
        _detailLoad = null;
    }

    private void ShowLoadingState()
    {
        LoadingPanel.Visibility = Visibility.Visible;
        ErrorPanel.Visibility = Visibility.Collapsed;
        DetailContent.Visibility = Visibility.Collapsed;
    }

    private void ShowLoadedState()
    {
        LoadingPanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        DetailContent.Visibility = Visibility.Visible;
    }

    private void ShowLoadFailure(string message, bool showFeedback)
    {
        _detail = null;
        LoadingPanel.Visibility = Visibility.Collapsed;
        DetailContent.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Visible;
        ErrorDetailText.Text = message;
        if (showFeedback)
        {
            Show(message, InfoBarSeverity.Error);
        }
    }

    private void ConfigureHandoffTargets()
    {
        var detected = new AgentCatalog().Detect()
            .Where(value => value.IsDetected)
            .ToArray();
        TargetAgentBox.ItemsSource = detected.Length > 0
            ? detected
            : new AgentCatalog().Detect();
        TargetAgentBox.SelectedIndex = 0;
    }

    private static string ConversationTitle(ConversationSummary conversation) =>
        string.IsNullOrWhiteSpace(conversation.Summary)
            ? LocalizationService.Get("UntitledConversation")
            : conversation.Summary;

    private void ApplyDetail(WebDavConversationDetail detail)
    {
        TitleText.Text = string.IsNullOrWhiteSpace(detail.Summary)
            ? LocalizationService.Get("UntitledConversation")
            : detail.Summary;
        var project = string.IsNullOrWhiteSpace(detail.ProjectDir)
            ? LocalizationService.Get("NotProvided")
            : detail.ProjectDir;
        MetadataText.Text = $"{detail.SourceAgent} · {project} · {FormatTimestamp(detail.UpdatedAt)}";
        MessageList.ItemsSource = detail.Messages
            .Select(value => new ConversationMessageRow(value))
            .ToArray();
        FileChangeList.ItemsSource = detail.FileChanges;
        MessageCountText.Text = detail.Messages.Count.ToString(
            CultureInfo.CurrentCulture);
        FileCountText.Text = detail.FileChanges.Count.ToString(
            CultureInfo.CurrentCulture);
        ToolCallCountText.Text = detail.Messages
            .Sum(value => value.ToolCalls.Count)
            .ToString(CultureInfo.CurrentCulture);
        NoMessagesPanel.Visibility = detail.Messages.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        NoFileChangesText.Visibility = detail.FileChanges.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        ResumeCommandBox.Text = detail.ResumeCommand ?? "";
        ResumePanel.Visibility = string.IsNullOrWhiteSpace(detail.ResumeCommand)
            ? Visibility.Collapsed
            : Visibility.Visible;
        StoragePathText.Text = string.IsNullOrWhiteSpace(detail.StoragePath)
            ? LocalizationService.Get("NotProvided")
            : detail.StoragePath;
    }

    private static string FormatTimestamp(string value) =>
        DateTimeOffset.TryParse(value, out var parsed)
            ? parsed.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
            : value;

    private async void Retry_Click(object sender, RoutedEventArgs args)
        => await LoadDetailAsync();

    private void StartAutomaticCapture()
    {
        _automaticCapture?.Cancel();
        _automaticCapture?.Dispose();
        _automaticCapture = new CancellationTokenSource();
        _ = RunAutomaticCaptureAsync(_automaticCapture.Token);
    }

    private async Task RunAutomaticCaptureAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(
                TimeSpan.FromMilliseconds(350),
                cancellationToken);
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_context is null) return;
                var settings = await _context.Window.Settings.LoadAsync(
                    cancellationToken);
                if (!settings.AutoCaptureMemory) return;
                try
                {
                    var result = await new AutomaticCaptureService(
                            _context.Window.Database,
                            _context.Window.Conversations)
                        .CaptureAsync(
                            _context.Conversation.SourceAgent,
                            _context.Conversation.Id,
                            cancellationToken);
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        _detail = result.Detail;
                        ApplyDetail(result.Detail);
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception exception)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"Automatic memory capture skipped: {exception}");
                }
                await Task.Delay(
                    TimeSpan.FromMinutes(2),
                    cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Leaving the conversation page intentionally stops capture.
        }
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

/// <summary>
/// Presentation row for a source-backed conversation message. Keeping the
/// original WebDAV payload intact while projecting it for XAML means a message
/// with tool calls is never reduced to plain transcript text in the desktop UI.
/// </summary>
public sealed class ConversationMessageRow
{
    public ConversationMessageRow(WebDavMessage value)
    {
        RoleLabel = LocalizeRole(value.Role);
        Content = value.Content ?? "";
        TimestampLabel = FormatTimestamp(value.Timestamp);
        ToolCalls = (value.ToolCalls ?? [])
            .Select(tool => new ConversationToolCallRow(tool))
            .ToArray();
    }

    public string RoleLabel { get; }
    public string Content { get; }
    public string TimestampLabel { get; }
    public IReadOnlyList<ConversationToolCallRow> ToolCalls { get; }
    public Visibility ContentVisibility => string.IsNullOrWhiteSpace(Content)
        ? Visibility.Collapsed
        : Visibility.Visible;
    public Visibility ToolCallsVisibility => ToolCalls.Count == 0
        ? Visibility.Collapsed
        : Visibility.Visible;
    public string ToolCallsHeader => LocalizationService.Format(
        "ConversationToolCallsCount",
        ToolCalls.Count);

    private static string LocalizeRole(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "user" => LocalizationService.Get("ConversationRoleUser"),
            "assistant" => LocalizationService.Get("ConversationRoleAssistant"),
            "system" => LocalizationService.Get("ConversationRoleSystem"),
            _ when string.IsNullOrWhiteSpace(value) =>
                LocalizationService.Get("NotProvided"),
            _ => value,
        };

    private static string FormatTimestamp(string value) =>
        DateTimeOffset.TryParse(value, out var parsed)
            ? parsed.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
            : value;
}

/// <summary>
/// A readable, copyable projection of a tool invocation. Input and output stay
/// in the original detail model for sync/MCP and are rendered here only for the
/// conversation screen.
/// </summary>
public sealed class ConversationToolCallRow
{
    public ConversationToolCallRow(WebDavToolCall value)
    {
        Name = string.IsNullOrWhiteSpace(value.Name)
            ? LocalizationService.Get("NotProvided")
            : value.Name;
        InputDetails = FormatJson(value.Input, indented: true);
        InputPreview = Compact(InputDetails);
        OutputText = value.Output ?? "";
        StatusLabel = string.IsNullOrWhiteSpace(value.Status)
            ? LocalizationService.Get("NotProvided")
            : value.Status;
    }

    public string Name { get; }
    public string InputPreview { get; }
    public string InputDetails { get; }
    public string OutputText { get; }
    public string StatusLabel { get; }
    public Visibility OutputVisibility => string.IsNullOrWhiteSpace(OutputText)
        ? Visibility.Collapsed
        : Visibility.Visible;
    public Visibility NoOutputVisibility => string.IsNullOrWhiteSpace(OutputText)
        ? Visibility.Visible
        : Visibility.Collapsed;

    private static string FormatJson(JsonElement value, bool indented)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
        {
            return LocalizationService.Get("NotProvided");
        }

        try
        {
            return JsonSerializer.Serialize(
                value,
                new JsonSerializerOptions { WriteIndented = indented });
        }
        catch (Exception)
        {
            return value.ToString();
        }
    }

    private static string Compact(string value)
    {
        var compact = string.Join(
            " ",
            value.Split(
                ['\r', '\n', '\t', ' '],
                StringSplitOptions.RemoveEmptyEntries));
        return compact.Length <= 180
            ? compact
            : compact[..177] + "…";
    }
}
