// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
using AIMemory.Core.Persistence;
using AIMemory.Core.Services;
using AIMemory.Windows.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;
using WinRT.Interop;

namespace AIMemory.Windows;

public sealed partial class AboutWindow : Window
{
    private static readonly Uri ProjectUri =
        new("https://github.com/douxy1994/AI-Memory");
    private readonly AIMemoryDatabase _database;
    private readonly SettingsStore _settings;
    private readonly CredentialService _credentials = new();
    private bool _checking;

    public AboutWindow(AIMemoryDatabase database, SettingsStore settings)
    {
        _database = database;
        _settings = settings;
        InitializeComponent();
        try
        {
            SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop
            {
                Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt,
            };
        }
        catch
        {
            // The themed background remains the fallback when Mica is disabled.
        }
        Title = LocalizationService.Get("AboutWindowTitle");
        ReleaseVersionText.Text = LocalizationService.Format(
            "ReleaseVersion",
            CurrentVersion());
        DevelopmentVersionText.Text = LocalizationService.Format(
            "DevelopmentVersion",
            DevelopmentVersion());
        ReleaseTagText.Text = $"v{CurrentVersion()}";
        AgentCoverageText.Text =
            LocalizationService.Format(
                "AgentCoverage",
                AgentCatalog.All.Count);

        var handle = WindowNative.GetWindowHandle(this);
        var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle);
        AppWindow.GetFromWindowId(id).Resize(
            new global::Windows.Graphics.SizeInt32(780, 900));
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            var settings = await _settings.LoadAsync();
            AutoUpdateToggle.IsOn = settings.AutoCheckUpdates;
            UpdateFeedBox.Text = settings.UpdateFeedUrl;
            await RefreshDiagnosticsAsync();
        }
        catch (Exception exception)
        {
            UpdateStatus.IsOpen = true;
            UpdateStatus.Severity = InfoBarSeverity.Error;
            UpdateStatus.Message = exception.Message;
        }
    }

    public async Task CheckForUpdatesAsync(bool automaticInstall)
    {
        if (_checking) return;
        _checking = true;
        CheckUpdateButton.IsEnabled = false;
        CheckUpdateLabel.Text =
            LocalizationService.Get("CheckingUpdates");
        UpdateStatus.IsOpen = true;
        UpdateStatus.Severity = InfoBarSeverity.Informational;
        UpdateStatus.Message =
            LocalizationService.Get("CheckingGitHubReleases");
        try
        {
            var settings = await _settings.LoadAsync();
            var result = await new UpdateService().CheckAsync(
                settings.UpdateFeedUrl,
                CurrentVersion());
            if (!result.IsUpdateAvailable)
            {
                UpdateStatus.Severity = InfoBarSeverity.Informational;
                UpdateStatus.Message = LocalizationService.Format(
                    "CurrentVersionLatest",
                    result.Release.Version);
                if (automaticInstall)
                {
                    await ShowUpdateResultAsync(
                        LocalizationService.Get("AlreadyLatestTitle"),
                        LocalizationService.Format(
                            "AlreadyLatestBody",
                            CurrentVersion()));
                }
                return;
            }

            UpdateStatus.Severity = InfoBarSeverity.Informational;
            UpdateStatus.Message = LocalizationService.Format(
                "NewVersionFound",
                result.Release.Version,
                result.Release.Title);
            if (!automaticInstall) return;

            UpdateStatus.Message = LocalizationService.Format(
                "DownloadingNewVersion",
                result.Release.Version);
            var path = await new UpdateService().DownloadAsync(
                result.Release,
                DataPaths.UpdateDirectory);
            var file = await StorageFile.GetFileFromPathAsync(path);
            if (!await Launcher.LaunchFileAsync(file))
            {
                throw new InvalidOperationException(
                    LocalizationService.Get("WindowsCannotOpenInstaller"));
            }
            UpdateStatus.Severity = InfoBarSeverity.Informational;
            UpdateStatus.Message =
                LocalizationService.Get("InstallerLaunched");
        }
        catch (Exception exception)
        {
            UpdateStatus.Severity = InfoBarSeverity.Error;
            UpdateStatus.Message = LocalizationService.Format(
                "UpdateCheckFailed",
                exception.Message);
            if (automaticInstall)
            {
                await ShowUpdateResultAsync(
                    LocalizationService.Get("CannotCheckUpdatesTitle"),
                    exception.Message);
            }
        }
        finally
        {
            _checking = false;
            CheckUpdateButton.IsEnabled = true;
            CheckUpdateLabel.Text =
                LocalizationService.Get("CheckForUpdates");
        }
    }

    private async void GitHub_Click(object sender, RoutedEventArgs args) =>
        await Launcher.LaunchUriAsync(ProjectUri);

    private async void CheckUpdate_Click(object sender, RoutedEventArgs args)
    {
        if (await SaveUpdateSettingsAsync(showConfirmation: false))
        {
            await CheckForUpdatesAsync(automaticInstall: false);
        }
    }

    private async void SaveUpdateSettings_Click(
        object sender,
        RoutedEventArgs args) =>
        await SaveUpdateSettingsAsync(showConfirmation: true);

    private async Task<bool> SaveUpdateSettingsAsync(bool showConfirmation)
    {
        try
        {
            var settings = await _settings.LoadAsync();
            settings.AutoCheckUpdates = AutoUpdateToggle.IsOn;
            settings.UpdateFeedUrl = UpdateFeedBox.Text.Trim();
            await _settings.SaveAsync(settings);
            if (showConfirmation)
            {
                UpdateStatus.IsOpen = true;
                UpdateStatus.Severity = InfoBarSeverity.Informational;
                UpdateStatus.Message = LocalizationService.Get(
                    "UpdateSettingsSaved");
            }
            return true;
        }
        catch (Exception exception)
        {
            UpdateStatus.IsOpen = true;
            UpdateStatus.Severity = InfoBarSeverity.Error;
            UpdateStatus.Message = LocalizationService.Format(
                "UpdateSettingsSaveFailed", exception.Message);
            return false;
        }
    }

    private async void RunReadiness_Click(object sender, RoutedEventArgs args)
    {
        RunReadinessButton.IsEnabled = false;
        ReadinessProgress.IsActive = true;
        ReadinessProgress.Visibility = Visibility.Visible;
        try
        {
            var report = await new UpgradeReadinessService(
                _database,
                _settings,
                DataPaths.SettingsPath).CheckAsync(username =>
            {
                var stored = _credentials.Load();
                return stored is not null
                    && string.Equals(
                        stored.Value.Username,
                        username,
                        StringComparison.Ordinal)
                    && !string.IsNullOrEmpty(stored.Value.Password);
            });
            ReadinessList.ItemsSource = report.Checks
                .Select(value => new LocalizedUpgradeReadinessCheck(value))
                .ToArray();
            ReadinessSummary.Text = report.Status switch
            {
                "error" => LocalizationService.Format(
                    "UpgradeReadinessErrors", report.ErrorCount),
                "warning" => LocalizationService.Format(
                    "UpgradeReadinessWarnings", report.WarningCount),
                _ => LocalizationService.Get("UpgradeReadinessPassed"),
            };
            ReadinessSummary.Visibility = Visibility.Visible;
            UpdateStatus.IsOpen = true;
            UpdateStatus.Severity = report.Status switch
            {
                "error" => InfoBarSeverity.Error,
                "warning" => InfoBarSeverity.Warning,
                _ => InfoBarSeverity.Informational,
            };
            UpdateStatus.Message = ReadinessSummary.Text;
        }
        catch (Exception exception)
        {
            ReadinessSummary.Text = LocalizationService.Format(
                "UpgradeReadinessFailed", exception.Message);
            ReadinessSummary.Visibility = Visibility.Visible;
            UpdateStatus.IsOpen = true;
            UpdateStatus.Severity = InfoBarSeverity.Error;
            UpdateStatus.Message = ReadinessSummary.Text;
        }
        finally
        {
            RunReadinessButton.IsEnabled = true;
            ReadinessProgress.IsActive = false;
            ReadinessProgress.Visibility = Visibility.Collapsed;
        }
    }

    private async void RefreshDiagnostics_Click(
        object sender,
        RoutedEventArgs args) =>
        await RefreshDiagnosticsAsync();

    private async Task RefreshDiagnosticsAsync()
    {
        try
        {
            var report = await new DiagnosticsService(_database)
                .CollectAsync(CurrentVersion());
            DiagnosticsBox.Text = report.ToDisplayText();
        }
        catch (Exception exception)
        {
            DiagnosticsBox.Text = LocalizationService.Format(
                "DiagnosticsReadFailed", exception.Message);
        }
    }

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs args)
    {
        var package = new DataPackage();
        package.SetText(DiagnosticsBox.Text ?? "");
        Clipboard.SetContent(package);
        Clipboard.Flush();
        UpdateStatus.IsOpen = true;
        UpdateStatus.Severity = InfoBarSeverity.Informational;
        UpdateStatus.Message = LocalizationService.Get("DiagnosticsCopied");
    }

    private async void OpenDataDirectory_Click(
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
                    LocalizationService.Format(
                        "WindowsCannotOpenLocation",
                        LocalizationService.Get("DataDirectory")));
            }
        }
        catch (Exception exception)
        {
            UpdateStatus.IsOpen = true;
            UpdateStatus.Severity = InfoBarSeverity.Error;
            UpdateStatus.Message = LocalizationService.Format(
                "OpenLocationFailed",
                LocalizationService.Get("DataDirectory"),
                exception.Message);
        }
    }

    private async Task ShowUpdateResultAsync(
        string title,
        string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = title,
            Content = message,
            CloseButtonText = LocalizationService.Get("Done"),
            DefaultButton = ContentDialogButton.Close,
        };
        await dialog.ShowAsync();
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
            return typeof(AboutWindow).Assembly.GetName().Version?
                .ToString(3) ?? "0.1.3";
        }
    }

    private static string DevelopmentVersion()
    {
        try
        {
            var path = Path.Combine(
                AppContext.BaseDirectory,
                "AIMemorySourceRevision.txt");
            var revision = File.ReadAllText(path).Trim();
            return string.IsNullOrWhiteSpace(revision)
                ? LocalizationService.Get("UncommittedBuild")
                : revision;
        }
        catch
        {
            return LocalizationService.Get("UncommittedBuild");
        }
    }
}
