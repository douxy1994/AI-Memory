using AIMemory.Core.Persistence;
using AIMemory.Core.Services;
using AIMemory.Windows.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel;
using Windows.Storage;
using Windows.System;
using WinRT.Interop;

namespace AIMemory.Windows;

public sealed partial class AboutWindow : Window
{
    private static readonly Uri ProjectUri =
        new("https://github.com/douxy1994/AI-Memory");
    private readonly SettingsStore _settings;
    private bool _checking;

    public AboutWindow(SettingsStore settings)
    {
        _settings = settings;
        InitializeComponent();
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
            new global::Windows.Graphics.SizeInt32(620, 720));
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
                UpdateStatus.Severity = InfoBarSeverity.Success;
                UpdateStatus.Message = LocalizationService.Format(
                    "CurrentVersionLatest",
                    result.Release.Version);
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
            UpdateStatus.Severity = InfoBarSeverity.Success;
            UpdateStatus.Message =
                LocalizationService.Get("InstallerLaunched");
        }
        catch (Exception exception)
        {
            UpdateStatus.Severity = InfoBarSeverity.Error;
            UpdateStatus.Message = LocalizationService.Format(
                "UpdateCheckFailed",
                exception.Message);
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

    private async void CheckUpdate_Click(object sender, RoutedEventArgs args) =>
        await CheckForUpdatesAsync(automaticInstall: false);

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
                .ToString(3) ?? "0.1.0";
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
