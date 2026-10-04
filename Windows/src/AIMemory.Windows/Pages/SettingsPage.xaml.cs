// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
using AIMemory.Core.Models;
using AIMemory.Core.Persistence;
using AIMemory.Core.Services;
using AIMemory.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Windows.Storage.Pickers;
using Windows.ApplicationModel;
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
    private readonly CloudReadinessService _cloudReadiness = new();
    private bool _loading;
    private bool _updatingStartup;
    private bool _categorySelectionChanging;

    public SettingsPage() => InitializeComponent();

    protected override async void OnNavigatedTo(NavigationEventArgs args)
    {
        var navigation = args.Parameter as SettingsNavigation;
        _window = navigation?.Window ?? args.Parameter as MainWindow;
        if (_window is null) return;
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
            DatabasePathText.Text = LocalizationService.Format(
                "DatabaseFilePath",
                DataPaths.DatabasePath);
            SettingsPathText.Text = LocalizationService.Format(
                "SettingsFilePath",
                DataPaths.SettingsPath);
            DataPathText.Text = LocalizationService.Format(
                "DataDirectoryPath",
                DataPaths.SupportDirectory);
            var importer = new ChatMemImportService(_window.Database);
            var chatMemSource = importer.FindSource();
            ChatMemSourceText.Text = chatMemSource is null
                ? LocalizationService.Get("ChatMemSourceNotDetected")
                : LocalizationService.Format(
                    "ChatMemSourceDetected",
                    chatMemSource);
            SelectCategory(navigation?.Category ?? "general");
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
        if (_categorySelectionChanging) return;
        if (SettingsCategories.SelectedItem is ListViewItem item)
        {
            ApplyCategorySelection(item.Tag as string ?? "general");
        }
    }

    private void SettingsCategoryPicker_SelectionChanged(
        object sender,
        SelectionChangedEventArgs args)
    {
        if (_categorySelectionChanging) return;
        if (SettingsCategoryPicker.SelectedItem is ComboBoxItem item)
        {
            ApplyCategorySelection(item.Tag as string ?? "general");
        }
    }

    private void SettingsRoot_SizeChanged(
        object sender,
        SizeChangedEventArgs args)
    {
        var compact = args.NewSize.Width < 950;
        SettingsRoot.Padding = new Thickness(0);
        SettingsContentGrid.ColumnSpacing = 0;
        SettingsContentScroll.Padding = compact
            ? new Thickness(18, 20, 18, 20)
            : new Thickness(42, 34, 42, 34);
        SettingsCategories.Visibility = compact
            ? Visibility.Collapsed
            : Visibility.Visible;
        SettingsCategoryPicker.Visibility = compact
            ? Visibility.Visible
            : Visibility.Collapsed;

        Grid.SetRow(SettingsCategoryCard, 0);
        Grid.SetColumn(SettingsCategoryCard, 0);
        Grid.SetColumnSpan(SettingsCategoryCard, compact ? 2 : 1);
        Grid.SetRowSpan(SettingsCategoryCard, compact ? 1 : 3);
        Grid.SetRow(SettingsContentScroll, compact ? 2 : 0);
        Grid.SetColumn(SettingsContentScroll, compact ? 0 : 1);
        Grid.SetColumnSpan(SettingsContentScroll, compact ? 2 : 1);
        Grid.SetRowSpan(SettingsContentScroll, compact ? 1 : 3);
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
    }

    private void SelectCategory(string requestedCategory)
    {
        var item = SettingsCategories.Items
            .OfType<ListViewItem>()
            .FirstOrDefault(value => string.Equals(
                value.Tag as string,
                requestedCategory,
                StringComparison.Ordinal));
        item ??= SettingsCategories.Items
            .OfType<ListViewItem>()
            .FirstOrDefault();
        if (item is null) return;
        ApplyCategorySelection(item.Tag as string ?? "general");
    }

    private void ApplyCategorySelection(string category)
    {
        _categorySelectionChanging = true;
        try
        {
            SettingsCategories.SelectedItem = SettingsCategories.Items
                .OfType<ListViewItem>()
                .FirstOrDefault(value => string.Equals(
                    value.Tag as string,
                    category,
                    StringComparison.Ordinal));
            SettingsCategoryPicker.SelectedItem = SettingsCategoryPicker.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(value => string.Equals(
                    value.Tag as string,
                    category,
                    StringComparison.Ordinal));
            ShowCategory(category);
        }
        finally
        {
            _categorySelectionChanging = false;
        }
    }

    private async Task ReloadStartupAsync()
    {
        _updatingStartup = true;
        try
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
            StartupToggle.IsEnabled = state is not (
                StartupTaskState.DisabledByPolicy
                or StartupTaskState.DisabledByUser);
            OpenStartupSettingsButton.Visibility =
                state == StartupTaskState.DisabledByUser
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }
        catch (Exception exception)
        {
            StartupToggle.IsOn = false;
            StartupToggle.IsEnabled = false;
            StartupDetail.Text = LocalizationService.Format(
                "StartupSettingFailed",
                exception.Message);
            OpenStartupSettingsButton.Visibility = Visibility.Visible;
        }
        finally
        {
            _updatingStartup = false;
        }
    }

    private async void StartupToggle_Toggled(object sender, RoutedEventArgs args)
    {
        if (_loading || _updatingStartup) return;
        try
        {
            var requested = StartupToggle.IsOn;
            var state = await _startup.SetEnabledAsync(requested);
            await ReloadStartupAsync();
            if (requested && state != StartupTaskState.Enabled)
            {
                throw new InvalidOperationException(
                    LocalizationService.Get(
                        state == StartupTaskState.DisabledByUser
                            ? "StartupDisabledByUser"
                            : "StartupDisabledByPolicy"));
            }
            Show(
                LocalizationService.Get("StartupSettingUpdated"),
                InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            await ReloadStartupAsync();
            Show(
                LocalizationService.Format(
                    "StartupSettingFailed",
                    exception.Message),
                InfoBarSeverity.Error);
        }
    }

    private async void OpenStartupSettings_Click(
        object sender,
        RoutedEventArgs args)
    {
        try
        {
            if (!await _startup.OpenSystemSettingsAsync())
            {
                throw new InvalidOperationException(
                    LocalizationService.Get("StartupSettingsLaunchRejected"));
            }
        }
        catch (Exception exception)
        {
            Show(
                LocalizationService.Format(
                    "StartupSettingsLaunchFailed",
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

    private async void InstallAllAgents_Click(
        object sender,
        RoutedEventArgs args) =>
        await RunBulkAgentIntegrationAsync(enabled: true);

    private async void UninstallAllAgents_Click(
        object sender,
        RoutedEventArgs args)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = LocalizationService.Get("AgentUninstallAllTitle"),
            Content = LocalizationService.Get("AgentUninstallAllDescription"),
            PrimaryButtonText = LocalizationService.Get("UninstallAll"),
            CloseButtonText = LocalizationService.Get("Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await RunBulkAgentIntegrationAsync(enabled: false);
        }
    }

    private async Task RunBulkAgentIntegrationAsync(bool enabled)
    {
        var targets = _agentIntegrations.Detect()
            .Where(value =>
                value.CanToggle
                && (enabled
                    ? !value.IsIntegrated
                    : value.IsIntegrated
                      || value.State == AgentIntegrationState.Partial))
            .ToArray();
        if (targets.Length == 0)
        {
            Show(
                LocalizationService.Get("NoEligibleAgentIntegrations"),
                InfoBarSeverity.Warning);
            return;
        }

        AgentCommandBar.IsEnabled = false;
        AgentList.IsEnabled = false;
        try
        {
            var result = await Task.Run(() =>
            {
                var updated = 0;
                var failures = new List<string>();
                foreach (var target in targets)
                {
                    try
                    {
                        _agentIntegrations.SetEnabled(target, enabled);
                        updated += 1;
                    }
                    catch (Exception exception)
                    {
                        failures.Add($"{target.Label}: {exception.Message}");
                    }
                }
                return (updated, failures);
            });
            ReloadAgents();

            var summary = LocalizationService.Format(
                enabled
                    ? "AgentBulkInstallCompleted"
                    : "AgentBulkUninstallCompleted",
                result.updated);
            if (result.failures.Count == 0)
            {
                Show(summary, InfoBarSeverity.Success);
            }
            else
            {
                Show(
                    LocalizationService.Format(
                        "AgentBulkPartialFailure",
                        summary,
                        result.failures.Count,
                        string.Join(
                            LocalizationService.Get("ListSeparator"),
                            result.failures)),
                    InfoBarSeverity.Warning);
            }
        }
        finally
        {
            AgentCommandBar.IsEnabled = true;
            AgentList.IsEnabled = true;
        }
    }

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
        var enabled = !integration.IsIntegrated;
        try
        {
            _agentIntegrations.SetEnabled(integration, enabled);
            ReloadAgents();
            Show(
                enabled
                    ? LocalizationService.Format(
                        "AgentIntegrationEnabled",
                        integration.Label)
                    : LocalizationService.Format(
                        "AgentIntegrationDisabled",
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
        BeginSyncProgress(LocalizationService.Get("SyncProgressVerifying"));
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
            EndSyncProgress();
        }
    }

    private async void SyncWebDav_Click(object sender, RoutedEventArgs args)
    {
        if (_window is null) return;
        ApplyForm();
        BeginSyncProgress(LocalizationService.Get("SyncProgressPreparing"));
        try
        {
            var service = new WebDavService(_window.Conversations);
            var collection = WebDavService.BuildCollectionUri(_settings.Sync);
            var username = UsernameBox.Text.Trim();
            var password = PasswordBox.Password;
            var progress = new Progress<SyncProgress>(ApplySyncProgress);
            // SQLite export and semantic hashing are CPU-heavy for a real
            // multi-agent history. Start the complete operation on the thread
            // pool so WinUI remains clickable while progress is marshalled
            // back to this page.
            var result = await Task.Run(() => service.SyncAsync(
                collection,
                username,
                password,
                progress));
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
            EndSyncProgress();
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
            BeginSyncProgress(LocalizationService.Get("SyncProgressPreparing"));
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
            EndSyncProgress();
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

    private async void ChooseLocalFolder_Click(
        object sender,
        RoutedEventArgs args)
    {
        if (_window is null) return;
        try
        {
            var picker = new FolderPicker(_window.WindowId)
            {
                Title = LocalizationService.Get("ChooseSyncFolderTitle"),
                CommitButtonText =
                    LocalizationService.Get("ChooseSyncFolderCommit"),
                SettingsIdentifier = "AIMemory.SyncFolder",
            };
            var result = await picker.PickSingleFolderAsync();
            if (result is null) return;
            SyncFolderBox.Text = result.Path;
            await SaveLocalFolderAsync(showConfirmation: true);
        }
        catch (Exception exception)
        {
            Show(
                LocalizationService.Format(
                    "ChooseSyncFolderFailed",
                    exception.Message),
                InfoBarSeverity.Error);
        }
    }

    private async void SaveLocalFolder_Click(object sender, RoutedEventArgs args) =>
        await SaveLocalFolderAsync(showConfirmation: true);

    private async Task<bool> SaveLocalFolderAsync(bool showConfirmation)
    {
        if (_window is null) return false;
        try
        {
            _settings.Sync.Provider = "local";
            _settings.Sync.SyncFolder = SyncFolderBox.Text.Trim();
            await _window.Settings.SaveAsync(_settings);
            if (showConfirmation)
            {
                Show(
                    LocalizationService.Get("LocalSyncFolderSaved"),
                    InfoBarSeverity.Success);
            }
            return true;
        }
        catch (Exception exception)
        {
            Show(
                LocalizationService.Format(
                    "LocalSyncFolderSaveFailed",
                    exception.Message),
                InfoBarSeverity.Error);
            return false;
        }
    }

    private void CheckCloudReadiness_Click(
        object sender,
        RoutedEventArgs args)
    {
        try
        {
            var result = _cloudReadiness.Check(
                SyncFolderBox.Text.Trim());
            var message = result.RecommendedAction switch
            {
                "folder_missing" =>
                    LocalizationService.Get("CloudFolderMissing"),
                "safe_to_sync" =>
                    LocalizationService.Get("CloudFolderReady"),
                _ when result.HasLockFiles =>
                    LocalizationService.Get("CloudFolderBusyWithLocks"),
                _ => LocalizationService.Get("CloudFolderRecentlyChanged"),
            };
            Show(
                message,
                result.IsQuiet
                    ? InfoBarSeverity.Success
                    : InfoBarSeverity.Warning);
        }
        catch (Exception exception)
        {
            Show(
                LocalizationService.Format(
                    "CloudReadinessCheckFailed",
                    exception.Message),
                InfoBarSeverity.Error);
        }
    }

    private async void SyncLocalFolder_Click(object sender, RoutedEventArgs args)
    {
        if (_window is null) return;
        if (!await SaveLocalFolderAsync(showConfirmation: false)) return;
        BeginSyncProgress(LocalizationService.Get("SyncProgressPreparing"));
        try
        {
            var service = new LocalFolderSyncService(_window.Conversations);
            var folder = SyncFolderBox.Text.Trim();
            var progress = new Progress<SyncProgress>(ApplySyncProgress);
            var result = await Task.Run(() => service.SyncAsync(
                folder,
                progress: progress));
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
        finally
        {
            EndSyncProgress();
        }
    }

    private void BeginSyncProgress(string message)
    {
        SyncProgressPanel.Visibility = Visibility.Visible;
        SyncProgressText.Text = message;
    }

    private void ApplySyncProgress(SyncProgress progress)
    {
        SyncProgressText.Text = string.IsNullOrWhiteSpace(progress.CurrentAgent)
            ? LocalizationService.Format(
                "SyncProgressCounters",
                progress.Uploaded,
                progress.Downloaded,
                progress.Skipped)
            : LocalizationService.Format(
                "SyncProgressConversation",
                progress.CurrentAgent,
                progress.CurrentConversationId ?? "",
                progress.Uploaded,
                progress.Downloaded,
                progress.Skipped);
    }

    private void EndSyncProgress()
    {
        SyncProgressPanel.Visibility = Visibility.Collapsed;
        SyncProgressText.Text = "";
    }

    private async void ImportChatMem_Click(object sender, RoutedEventArgs args)
    {
        if (_window is null) return;
        ImportChatMemButton.IsEnabled = false;
        try
        {
            await _window.ImportChatMemAsync();
        }
        finally
        {
            ImportChatMemButton.IsEnabled = true;
        }
    }

    private async void ImportNativeHistory_Click(object sender, RoutedEventArgs args)
    {
        if (_window is null) return;
        BeginSyncProgress(LocalizationService.Get("SyncProgressPreparing"));
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
            EndSyncProgress();
        }
    }

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

public sealed record SettingsNavigation(
    MainWindow Window,
    string Category);
