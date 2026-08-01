using AIMemory.Core.Persistence;
using AIMemory.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;

namespace AIMemory.Windows.Pages;

public sealed partial class HelpPage : Page
{
    private static readonly HelpTopicDefinition[] Topics =
    [
        new(
            "continue",
            "HelpTopicContinueTitle",
            "HelpTopicContinueDescription",
            "HelpTopicContinueAnswer",
            "HelpTopicContinueAction",
            HelpDestination.Workbench),
        new(
            "switch-agent",
            "HelpTopicSwitchAgentTitle",
            "HelpTopicSwitchAgentDescription",
            "HelpTopicSwitchAgentAnswer",
            "HelpTopicSwitchAgentAction",
            HelpDestination.Memory),
        new(
            "remembered",
            "HelpTopicRememberedTitle",
            "HelpTopicRememberedDescription",
            "HelpTopicRememberedAnswer",
            "HelpTopicRememberedAction",
            HelpDestination.Memory),
        new(
            "mcp",
            "HelpTopicMcpTitle",
            "HelpTopicMcpDescription",
            "HelpTopicMcpAnswer",
            "HelpTopicMcpAction",
            HelpDestination.AgentSettings),
        new(
            "start",
            "HelpTopicStartTitle",
            "HelpTopicStartDescription",
            "HelpTopicStartAnswer",
            "HelpTopicStartAction",
            HelpDestination.GeneralSettings),
        new(
            "sync",
            "HelpTopicSyncTitle",
            "HelpTopicSyncDescription",
            "HelpTopicSyncAnswer",
            "HelpTopicSyncAction",
            HelpDestination.SyncSettings),
    ];

    private MainWindow? _window;

    public HelpPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs args)
    {
        _window = args.Parameter as MainWindow;
        DatabasePathText.Text = DataPaths.DatabasePath;
        SettingsPathText.Text = DataPaths.SettingsPath;
        ReloadTopics();
    }

    private void SearchBox_TextChanged(
        object sender,
        TextChangedEventArgs args) => ReloadTopics();

    private void ReloadTopics()
    {
        var query = SearchBox.Text?.Trim() ?? string.Empty;
        var topics = Topics
            .Select(value => new HelpTopicRow(
                value,
                LocalizationService.Get(value.TitleKey),
                LocalizationService.Get(value.DescriptionKey),
                LocalizationService.Get(value.AnswerKey),
                LocalizationService.Get(value.ActionKey)))
            .Where(value => string.IsNullOrWhiteSpace(query)
                || value.Title.Contains(
                    query,
                    StringComparison.CurrentCultureIgnoreCase)
                || value.Description.Contains(
                    query,
                    StringComparison.CurrentCultureIgnoreCase)
                || value.Answer.Contains(
                    query,
                    StringComparison.CurrentCultureIgnoreCase))
            .ToArray();
        TopicList.ItemsSource = topics;
        NoResultsText.Visibility = topics.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OpenTopic_Click(object sender, RoutedEventArgs args)
    {
        if (_window is null
            || sender is not Button { Tag: HelpTopicRow topic })
        {
            return;
        }

        switch (topic.Value.Destination)
        {
            case HelpDestination.Workbench:
                _window.NavigateTo("workbench");
                break;
            case HelpDestination.Memory:
                _window.NavigateTo("memory");
                break;
            case HelpDestination.AgentSettings:
                _window.OpenSettingsCategory("agents");
                break;
            case HelpDestination.GeneralSettings:
                _window.OpenSettingsCategory("general");
                break;
            case HelpDestination.SyncSettings:
                _window.OpenSettingsCategory("sync");
                break;
        }
    }

    private async void OpenDataFolder_Click(
        object sender,
        RoutedEventArgs args)
    {
        try
        {
            Directory.CreateDirectory(DataPaths.SupportDirectory);
            var folder = await StorageFolder.GetFolderFromPathAsync(
                DataPaths.SupportDirectory);
            if (!await Launcher.LaunchFolderAsync(folder))
            {
                throw new InvalidOperationException(
                    LocalizationService.Get("HelpOpenDataFolderRejected"));
            }
        }
        catch (Exception exception)
        {
            Show(
                LocalizationService.Format(
                    "HelpOpenDataFolderFailed",
                    exception.Message),
                InfoBarSeverity.Error);
        }
    }

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs args)
    {
        var package = new DataPackage();
        package.SetText(string.Join(
            Environment.NewLine,
            LocalizationService.Format(
                "HelpDiagnosticDatabaseValue",
                DataPaths.DatabasePath),
            LocalizationService.Format(
                "HelpDiagnosticSettingsValue",
                DataPaths.SettingsPath),
            LocalizationService.Format(
                "HelpDiagnosticDataDirectoryValue",
                DataPaths.SupportDirectory)));
        Clipboard.SetContent(package);
        Clipboard.Flush();
        Show(
            LocalizationService.Get("HelpDiagnosticsCopied"),
            InfoBarSeverity.Success);
    }

    private void ReturnToWorkbench_Click(
        object sender,
        RoutedEventArgs args) => _window?.NavigateTo("workbench");

    private void Show(string message, InfoBarSeverity severity) =>
        FeedbackPresenter.Show(Feedback, message, severity);
}

public sealed record HelpTopicDefinition(
    string Id,
    string TitleKey,
    string DescriptionKey,
    string AnswerKey,
    string ActionKey,
    HelpDestination Destination);

public sealed record HelpTopicRow(
    HelpTopicDefinition Value,
    string Title,
    string Description,
    string Answer,
    string ActionLabel);

public enum HelpDestination
{
    Workbench,
    Memory,
    AgentSettings,
    GeneralSettings,
    SyncSettings,
}
