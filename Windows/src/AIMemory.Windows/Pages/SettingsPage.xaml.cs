using AIMemory.Core.Models;
using AIMemory.Core.Persistence;
using AIMemory.Core.Services;
using AIMemory.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;

namespace AIMemory.Windows.Pages;

public sealed partial class SettingsPage : Page
{
    private MainWindow? _window;
    private AppSettings _settings = new();
    private readonly StartupService _startup = new();
    private readonly CredentialService _credentials = new();
    private readonly AgentIntegrationService _agentIntegrations = new();
    private bool _loading;

    public SettingsPage() => InitializeComponent();

    protected override async void OnNavigatedTo(NavigationEventArgs args)
    {
        _window = (MainWindow)args.Parameter;
        _loading = true;
        try
        {
            _settings = await _window.Settings.LoadAsync();
            SchemeBox.SelectedIndex = _settings.Sync.WebdavScheme == "http" ? 1 : 0;
            HostBox.Text = _settings.Sync.WebdavHost;
            ServerPathBox.Text = _settings.Sync.WebdavPath;
            RemotePathBox.Text = _settings.Sync.RemotePath;
            UsernameBox.Text = _settings.Sync.Username;
            SyncFolderBox.Text = _settings.Sync.SyncFolder;
            AutoUpdateToggle.IsOn = _settings.AutoCheckUpdates;
            UpdateFeedBox.Text = _settings.UpdateFeedUrl;
            AutoCaptureToggle.IsOn = _settings.AutoCaptureMemory;
            AutoBackupToggle.IsOn = _settings.AutoBackupEnabled;
            AutoBackupIntervalBox.Value =
                _settings.AutoBackupIntervalMinutes;
            var languageOptions = new[]
            {
                new LocalizedOption(
                    "system",
                    LocalizationService.Get("LanguageSystem")),
                new LocalizedOption(
                    "zh-Hans",
                    LocalizationService.Get("LanguageChineseSimplified")),
                new LocalizedOption(
                    "en",
                    LocalizationService.Get("LanguageEnglish")),
            };
            LanguageBox.ItemsSource = languageOptions;
            LanguageBox.SelectedItem = languageOptions.First(option =>
                option.Id == LanguagePreferenceService.NormalizeId(
                    _settings.Language));
            var fontOptions = new[]
            {
                new LocalizedOption(
                    "system",
                    LocalizationService.Get("FontSystem")),
                new LocalizedOption(
                    "source-sans",
                    LocalizationService.Get("FontSourceSans")),
                new LocalizedOption(
                    "source-serif",
                    LocalizationService.Get("FontSourceSerif")),
                new LocalizedOption(
                    "wenkai",
                    LocalizationService.Get("FontWenkai")),
            };
            FontFamilyBox.ItemsSource = fontOptions;
            FontFamilyBox.SelectedItem = fontOptions.First(option =>
                option.Id == FontPreferenceService.NormalizeId(
                    _settings.FontFamily));
            if (_credentials.Load() is { } stored)
            {
                UsernameBox.Text = stored.Username;
                PasswordBox.Password = stored.Password;
            }
            await ReloadStartupAsync();
            ReloadAgents();
            DataPathText.Text = LocalizationService.Format(
                "DataDirectoryPath",
                DataPaths.SupportDirectory);
            var importer = new ChatMemImportService(_window.Database);
            ImportChatMemButton.IsEnabled = importer.FindSource() is not null;
            await RefreshDiagnosticsAsync();
            if (SettingsCategories.SelectedIndex < 0)
            {
                SettingsCategories.SelectedIndex = 0;
            }
            ShowCategory(
                (SettingsCategories.SelectedItem as ListViewItem)?.Tag as string
                ?? "general");
        }
        finally
        {
            _loading = false;
        }
    }

    private void SettingsCategories_SelectionChanged(
        object sender,
        SelectionChangedEventArgs args)
    {
        if (SettingsCategories.SelectedItem is ListViewItem item)
        {
            ShowCategory(item.Tag as string ?? "general");
        }
    }

    private void ShowCategory(string category)
    {
        GeneralPanel.Visibility = category == "general"
            ? Visibility.Visible
            : Visibility.Collapsed;
        AgentsPanel.Visibility = category == "agents"
            ? Visibility.Visible
            : Visibility.Collapsed;
        SyncPanel.Visibility = category == "sync"
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdatesPanel.Visibility = category == "updates"
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async Task ReloadStartupAsync()
    {
        var state = await _startup.GetStateAsync();
        StartupToggle.IsOn = state == StartupTaskState.Enabled;
        StartupDetail.Text = state switch
        {
            StartupTaskState.Enabled =>
                LocalizationService.Get("StartupEnabled"),
            StartupTaskState.DisabledByUser =>
                LocalizationService.Get("StartupDisabledByUser"),
            StartupTaskState.DisabledByPolicy =>
                LocalizationService.Get("StartupDisabledByPolicy"),
            _ => LocalizationService.Get("StartupDisabled"),
        };
        StartupToggle.IsEnabled = state != StartupTaskState.DisabledByPolicy;
    }

    private async void StartupToggle_Toggled(object sender, RoutedEventArgs args)
    {
        if (_loading) return;
        try
        {
            await _startup.SetEnabledAsync(StartupToggle.IsOn);
            await ReloadStartupAsync();
            Show(
                LocalizationService.Get("StartupSettingUpdated"),
                InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            Show(
                LocalizationService.Format(
                    "StartupSettingFailed",
                    exception.Message),
                InfoBarSeverity.Error);
        }
    }

    private async void SaveGeneralSettings_Click(
        object sender,
        RoutedEventArgs args)
    {
        if (_window is null) return;
        _settings.AutoCaptureMemory = AutoCaptureToggle.IsOn;
        _settings.AutoBackupEnabled = AutoBackupToggle.IsOn;
        _settings.AutoBackupIntervalMinutes = double.IsNaN(
                AutoBackupIntervalBox.Value)
            ? 30
            : (int)AutoBackupIntervalBox.Value;
        try
        {
            await _window.Settings.SaveAsync(_settings);
            _window.ConfigureAutomaticBackup(_settings);
            Show(
                LocalizationService.Get(
                    "AutomaticRecoverySettingsSaved"),
                InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            Show(LocalizationService.Format(
                    "AutomaticBackupSaveFailed",
                    exception.Message),
                InfoBarSeverity.Error);
        }
    }

    private async void FontFamilyBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs args)
    {
        if (_loading
            || _window is null
            || FontFamilyBox.SelectedItem is not LocalizedOption option)
        {
            return;
        }
        try
        {
            _settings.FontFamily = option.Id;
            await _window.Settings.SaveAsync(_settings);
            _window.ApplyFontFamily(option.Id);
            Show(
                LocalizationService.Format("FontApplied", option.Label),
                InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            Show(LocalizationService.Format(
                    "FontSaveFailed",
                    exception.Message),
                InfoBarSeverity.Error);
        }
    }

    private async void LanguageBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs args)
    {
        if (_loading
            || _window is null
            || LanguageBox.SelectedItem is not LocalizedOption option)
        {
            return;
        }
        try
        {
            _settings.Language = option.Id;
            await _window.Settings.SaveAsync(_settings);
            App.ApplyApplicationLanguage(option.Id);
            LanguageRestartHint.Text =
                LocalizationService.Get("LanguageRestartRequired");
            Show(
                LocalizationService.Get("LanguageSavedRestartRequired"),
                InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            Show(
                LocalizationService.Format(
                    "LanguageSaveFailed",
                    exception.Message),
                InfoBarSeverity.Error);
        }
    }

    private void DetectAgents_Click(object sender, RoutedEventArgs args) =>
        ReloadAgents();

    private void ReloadAgents() =>
        AgentList.ItemsSource = _agentIntegrations.Detect()
            .Select(value => new LocalizedAgentIntegration(value))
            .ToArray();

    private void ToggleAgent_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not Button
            {
                Tag: LocalizedAgentIntegration row,
            })
        {
            return;
        }
        var integration = row.Value;
        try
        {
            _agentIntegrations.SetEnabled(
                integration,
                !integration.IsIntegrated);
            ReloadAgents();
            Show(
                integration.IsIntegrated
                    ? LocalizationService.Format(
                        "AgentIntegrationDisabled",
                        integration.Label)
                    : LocalizationService.Format(
                        "AgentIntegrationEnabled",
                        integration.Label),
                InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            Show(
                LocalizationService.Format(
                    "AgentIntegrationUpdateFailed",
                    exception.Message),
                InfoBarSeverity.Error);
        }
    }

    private async void SaveSettings_Click(object sender, RoutedEventArgs args)
    {
        if (_window is null) return;
        ApplyForm();
        try
        {
            await _window.Settings.SaveAsync(_settings);
            _credentials.Save(UsernameBox.Text.Trim(), PasswordBox.Password);
            Show(
                LocalizationService.Get("SettingsSaved"),
                InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            Show(
                LocalizationService.Format(
                    "SettingsSaveFailed",
                    exception.Message),
                InfoBarSeverity.Error);
        }
    }

    private async void VerifyWebDav_Click(object sender, RoutedEventArgs args)
    {
        ApplyForm();
        SyncProgress.Visibility = Visibility.Visible;
        try
        {
            var status = await new WebDavService().VerifyAsync(
                WebDavService.BuildCollectionUri(_settings.Sync),
                UsernameBox.Text.Trim(),
                PasswordBox.Password);
            Show(
                LocalizationService.Format(
                    "WebDavVerificationSucceeded",
                    status),
                InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            Show(
                LocalizationService.Format(
                    "WebDavVerificationFailed",
                    exception.Message),
                InfoBarSeverity.Error);
        }
        finally
        {
            SyncProgress.Visibility = Visibility.Collapsed;
        }
    }

    private async void SyncWebDav_Click(object sender, RoutedEventArgs args)
    {
        if (_window is null) return;
        ApplyForm();
        SyncProgress.Visibility = Visibility.Visible;
        try
        {
            var service = new WebDavService(_window.Conversations);
            var result = await service.SyncAsync(
                WebDavService.BuildCollectionUri(_settings.Sync),
                UsernameBox.Text.Trim(),
                PasswordBox.Password);
            Show(
                LocalizationService.Format(
                    "SyncCompleted",
                    result.Uploaded,
                    result.Downloaded,
                    result.Skipped),
                InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            Show(
                LocalizationService.Format("SyncFailed", exception.Message),
                InfoBarSeverity.Error);
        }
        finally
        {
            SyncProgress.Visibility = Visibility.Collapsed;
        }
    }

    private async void CreateBackup_Click(object sender, RoutedEventArgs args)
    {
        if (_window is null) return;
        try
        {
            var result = await new BackupService(_window.Database)
                .CreateRecoveryPointDetailedAsync("manual");
            Show(
                result.Created
                    ? LocalizationService.Format(
                        "RecoveryPointCreated",
                        result.Path)
                    : LocalizationService.Format(
                        "RecoveryPointUnchanged",
                        result.Path),
                InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            Show(
                LocalizationService.Format(
                    "BackupFailed",
                    exception.Message),
                InfoBarSeverity.Error);
        }
    }

    private async void RestoreBackup_Click(object sender, RoutedEventArgs args)
    {
        if (_window is null) return;
        var service = new BackupService(_window.Database);
        var recoveryPoints = service.ListRecoveryPoints();
        if (recoveryPoints.Count == 0)
        {
            Show(
                LocalizationService.Get("NoRecoveryPoints"),
                InfoBarSeverity.Warning);
            return;
        }

        var picker = new ComboBox
        {
            ItemsSource = recoveryPoints,
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = LocalizationService.Get("RestoreRecoveryPointPrompt"),
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(picker);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = LocalizationService.Get("RestoreRecoveryPointTitle"),
            Content = content,
            PrimaryButtonText = LocalizationService.Get("Restore"),
            CloseButtonText = LocalizationService.Get("Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary
            || picker.SelectedItem is not string selected)
        {
            return;
        }

        try
        {
            SyncProgress.Visibility = Visibility.Visible;
            var safetyBackup = await service.RestoreRecoveryPointAsync(selected);
            await ReloadRestoredSettingsAsync();
            Show(
                LocalizationService.Format(
                    "RestoreCompleted",
                    safetyBackup),
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
        finally
        {
            SyncProgress.Visibility = Visibility.Collapsed;
        }
    }

    private async Task ReloadRestoredSettingsAsync()
    {
        if (_window is null) return;
        _settings = await _window.Settings.LoadAsync();
        SchemeBox.SelectedIndex =
            _settings.Sync.WebdavScheme == "http" ? 1 : 0;
        HostBox.Text = _settings.Sync.WebdavHost;
        ServerPathBox.Text = _settings.Sync.WebdavPath;
        RemotePathBox.Text = _settings.Sync.RemotePath;
        UsernameBox.Text = _settings.Sync.Username;
        SyncFolderBox.Text = _settings.Sync.SyncFolder;
        AutoUpdateToggle.IsOn = _settings.AutoCheckUpdates;
        UpdateFeedBox.Text = _settings.UpdateFeedUrl;
        AutoCaptureToggle.IsOn = _settings.AutoCaptureMemory;
        AutoBackupToggle.IsOn = _settings.AutoBackupEnabled;
        AutoBackupIntervalBox.Value =
            _settings.AutoBackupIntervalMinutes;
        _window.ConfigureAutomaticBackup(_settings);
        if (_credentials.Load() is { } stored)
        {
            UsernameBox.Text = stored.Username;
            PasswordBox.Password = stored.Password;
        }
    }

    private async void SaveLocalFolder_Click(object sender, RoutedEventArgs args)
    {
        if (_window is null) return;
        _settings.Sync.Provider = "local";
        _settings.Sync.SyncFolder = SyncFolderBox.Text.Trim();
        await _window.Settings.SaveAsync(_settings);
        Show(
            LocalizationService.Get("LocalSyncFolderSaved"),
            InfoBarSeverity.Success);
    }

    private async void SyncLocalFolder_Click(object sender, RoutedEventArgs args)
    {
        if (_window is null) return;
        try
        {
            var result = await new LocalFolderSyncService(_window.Conversations)
                .SyncAsync(SyncFolderBox.Text.Trim());
            Show(
                LocalizationService.Format(
                    "LocalSyncCompleted",
                    result.Uploaded,
                    result.Downloaded,
                    result.Skipped),
                InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            Show(
                LocalizationService.Format(
                    "LocalSyncFailed",
                    exception.Message),
                InfoBarSeverity.Error);
        }
    }

    private async void ImportChatMem_Click(object sender, RoutedEventArgs args)
    {
        if (_window is null) return;
        var importer = new ChatMemImportService(_window.Database);
        var source = importer.FindSource();
        if (source is null)
        {
            Show(
                LocalizationService.Get("ChatMemDatabaseNotFound"),
                InfoBarSeverity.Warning);
            return;
        }
        try
        {
            var backup = await importer.ImportAsync(source);
            Show(
                string.IsNullOrWhiteSpace(backup)
                    ? LocalizationService.Get("ChatMemImportCompleted")
                    : LocalizationService.Format(
                        "ChatMemImportCompletedWithBackup",
                        backup),
                InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            Show(
                LocalizationService.Format(
                    "ImportFailed",
                    exception.Message),
                InfoBarSeverity.Error);
        }
    }

    private async void ImportNativeHistory_Click(object sender, RoutedEventArgs args)
    {
        if (_window is null) return;
        SyncProgress.Visibility = Visibility.Visible;
        try
        {
            var report = await new NativeHistoryImportService(
                _window.Conversations).ImportAllAsync();
            var details = string.Join(
                "，",
                report.Imported.Select(value => $"{value.Key} {value.Value}"));
            Show(
                report.Warnings.Count == 0
                    ? LocalizationService.Format(
                        "NativeHistoryImportCompleted",
                        details)
                    : LocalizationService.Format(
                        "NativeHistoryImportCompletedWithWarnings",
                        details,
                        report.Warnings.Count),
                report.Warnings.Count == 0
                    ? InfoBarSeverity.Success
                    : InfoBarSeverity.Warning);
        }
        catch (Exception exception)
        {
            Show(
                LocalizationService.Format(
                    "NativeHistoryImportFailed",
                    exception.Message),
                InfoBarSeverity.Error);
        }
        finally
        {
            SyncProgress.Visibility = Visibility.Collapsed;
        }
    }

    private async void SaveUpdateSettings_Click(
        object sender,
        RoutedEventArgs args)
    {
        if (_window is null) return;
        _settings.AutoCheckUpdates = AutoUpdateToggle.IsOn;
        _settings.UpdateFeedUrl = UpdateFeedBox.Text.Trim();
        try
        {
            await _window.Settings.SaveAsync(_settings);
            Show(
                LocalizationService.Get("UpdateSettingsSaved"),
                InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            Show(
                LocalizationService.Format(
                    "UpdateSettingsSaveFailed",
                    exception.Message),
                InfoBarSeverity.Error);
        }
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs args)
    {
        if (_window is null) return;
        _settings.AutoCheckUpdates = AutoUpdateToggle.IsOn;
        _settings.UpdateFeedUrl = UpdateFeedBox.Text.Trim();
        try
        {
            await _window.Settings.SaveAsync(_settings);
            UpdateStatusText.Text =
                LocalizationService.Get("OpenedAboutCheckingUpdates");
            _window.OpenAboutAndCheckForUpdates();
        }
        catch (Exception exception)
        {
            UpdateStatusText.Text = LocalizationService.Format(
                "UpdateSettingsSaveFailed",
                exception.Message);
            Show(UpdateStatusText.Text, InfoBarSeverity.Error);
        }
    }

    private async void RefreshDiagnostics_Click(
        object sender,
        RoutedEventArgs args) =>
        await RefreshDiagnosticsAsync();

    private async Task RefreshDiagnosticsAsync()
    {
        if (_window is null) return;
        try
        {
            var report = await new DiagnosticsService(_window.Database)
                .CollectAsync(CurrentVersion());
            DiagnosticsBox.Text = report.ToDisplayText();
        }
        catch (Exception exception)
        {
            DiagnosticsBox.Text = LocalizationService.Format(
                "DiagnosticsReadFailed",
                exception.Message);
        }
    }

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs args)
    {
        var package = new DataPackage();
        package.SetText(DiagnosticsBox.Text ?? "");
        Clipboard.SetContent(package);
        Clipboard.Flush();
        Show(
            LocalizationService.Get("DiagnosticsCopied"),
            InfoBarSeverity.Success);
    }

    private async void OpenDataDirectory_Click(
        object sender,
        RoutedEventArgs args)
        => await OpenDirectoryAsync(
            DataPaths.SupportDirectory,
            LocalizationService.Get("DataDirectory"));

    private async void OpenBackupDirectory_Click(
        object sender,
        RoutedEventArgs args)
        => await OpenDirectoryAsync(
            DataPaths.BackupDirectory,
            LocalizationService.Get("BackupDirectory"));

    private async Task OpenDirectoryAsync(
        string path,
        string label)
    {
        try
        {
            Directory.CreateDirectory(path);
            var folder = await StorageFolder.GetFolderFromPathAsync(path);
            if (!await Launcher.LaunchFolderAsync(folder))
            {
                throw new InvalidOperationException(
                    LocalizationService.Format(
                        "WindowsCannotOpenLocation",
                        label));
            }
        }
        catch (Exception exception)
        {
            Show(LocalizationService.Format(
                    "OpenLocationFailed",
                    label,
                    exception.Message),
                InfoBarSeverity.Error);
        }
    }

    private static string CurrentVersion()
    {
        try
        {
            var version = Package.Current.Id.Version;
            return $"{version.Major}.{version.Minor}.{version.Build}";
        }
        catch
        {
            return typeof(SettingsPage).Assembly.GetName().Version?
                .ToString(3) ?? "0.1.0";
        }
    }

    private void ApplyForm()
    {
        _settings.Sync.Provider = "webdav";
        _settings.Sync.WebdavScheme =
            ((ComboBoxItem)SchemeBox.SelectedItem).Content.ToString() ?? "https";
        _settings.Sync.WebdavHost = HostBox.Text.Trim();
        _settings.Sync.WebdavPath = ServerPathBox.Text.Trim();
        _settings.Sync.RemotePath = string.IsNullOrWhiteSpace(RemotePathBox.Text)
            ? "chatmem"
            : RemotePathBox.Text.Trim();
        _settings.Sync.Username = UsernameBox.Text.Trim();
    }

    private void Show(string message, InfoBarSeverity severity)
        => FeedbackPresenter.Show(
            Feedback,
            message,
            severity);
}
