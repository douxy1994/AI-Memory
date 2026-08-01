using AIMemory.Core.Persistence;
using AIMemory.Core.Services;
using AIMemory.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.Globalization;

namespace AIMemory.Windows;

public sealed partial class App : Application
{
    private readonly AppInstance _instance;
    private MainWindow? _window;
    private bool _activationPending;

    public App(AppInstance instance)
    {
        _instance = instance;
        InitializeComponent();
        _instance.Activated += OnActivated;
        UnhandledException += (_, eventArgs) =>
        {
            System.Diagnostics.Debug.WriteLine(eventArgs.Exception);
        };
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        StartupDiagnostics.Reset();
        StartupDiagnostics.Write("launch.begin");
        try
        {
            DataPaths.EnsureDirectories();
            StartupDiagnostics.Write("directories.ready");
            var database = new AIMemoryDatabase();
            var settingsStore = new SettingsStore();
            _window = new MainWindow(database);
            StartupDiagnostics.Write("window.created");
            _window.Activate();
            StartupDiagnostics.Write("window.activated");
            await database.InitializeAsync();
            StartupDiagnostics.Write("database.ready");
            var settings = await settingsStore.LoadAsync();
            StartupDiagnostics.Write("settings.ready");
            ApplyApplicationLanguage(settings.Language);
            _window.CompleteStartup(settings);
            StartupDiagnostics.Write("shell.ready");
            if (_activationPending)
            {
                _activationPending = false;
                _window.BringToFront();
                StartupDiagnostics.Write("activation.replayed");
            }
            _window.ConfigureAutomaticBackup(settings);
            // Do compatibility migration after the first window is visible.  A
            // stale ChatMem profile or credential provider must not delay the
            // Windows shell, single-instance activation, or the workbench.
            _ = ImportChatMemWebDavAfterLaunchAsync(settingsStore);
            _ = CheckForUpdatesAtLaunchAsync();
            StartupDiagnostics.Write("launch.complete");
        }
        catch (Exception exception)
        {
            StartupDiagnostics.Write("launch.failed", exception);
            System.Diagnostics.Debug.WriteLine(
                $"AI Memory launch failed: {exception}");
            _window?.ShowStartupFailure(exception);
        }
    }

    private async Task ImportChatMemWebDavAfterLaunchAsync(
        SettingsStore settingsStore)
    {
        ChatMemWebDavImportResult? result = null;
        string? error = null;
        try
        {
            var credentials = new CredentialService();
            result = await new ChatMemWebDavImportService(settingsStore)
                .ImportAsync(
                    username => credentials.Load(username)?.Password,
                    credentials.LoadLegacyChatMemPassword,
                    credentials.Save);
        }
        catch (Exception exception)
        {
            error = exception.Message;
            System.Diagnostics.Debug.WriteLine(
                $"ChatMem WebDAV import failed: {exception}");
        }

        var window = _window;
        if (window is null) return;
        window.DispatcherQueue.TryEnqueue(() =>
            ShowChatMemWebDavImportFeedback(result, error));
    }

    private void ShowChatMemWebDavImportFeedback(
        ChatMemWebDavImportResult? result,
        string? error)
    {
        if (_window is null) return;
        if (!string.IsNullOrWhiteSpace(error))
        {
            _window.ShowFeedback(
                LocalizationService.Format(
                    "ChatMemWebDavImportFailed",
                    error),
                InfoBarSeverity.Error);
            return;
        }
        if (result is null) return;
        if (result.MissingUsername)
        {
            _window.ShowFeedback(
                LocalizationService.Get(
                    "ChatMemWebDavImportedWithoutUsername"),
                InfoBarSeverity.Warning);
        }
        else if (result.MissingCredential)
        {
            _window.ShowFeedback(
                LocalizationService.Get(
                    "ChatMemWebDavImportedWithoutPassword"),
                InfoBarSeverity.Warning);
        }
        else if (result.Changed)
        {
            _window.ShowFeedback(
                LocalizationService.Get("ChatMemWebDavImported"),
                InfoBarSeverity.Success);
        }
    }

    public static void ApplyApplicationFont(string preference)
    {
        Current.Resources["ContentControlThemeFontFamily"] =
            new FontFamily(
                FontPreferenceService.ResolveWindowsFamily(preference));
    }

    public static void ApplyApplicationLanguage(string preference)
    {
        ApplicationLanguages.PrimaryLanguageOverride =
            LanguagePreferenceService.ResolveWindowsLanguageTag(preference);
    }

    private async Task CheckForUpdatesAtLaunchAsync()
    {
        if (_window is null) return;
        try
        {
            var settings = await _window.Settings.LoadAsync();
            if (!settings.AutoCheckUpdates
                || string.IsNullOrWhiteSpace(settings.UpdateFeedUrl))
            {
                return;
            }
            var version = typeof(App).Assembly.GetName().Version?
                .ToString(3) ?? "0.1.0";
            var result = await new UpdateService().CheckAsync(
                settings.UpdateFeedUrl,
                version);
            if (result.IsUpdateAvailable)
            {
                _window.ShowFeedback(
                    Services.LocalizationService.Format(
                        "UpdateAvailableAtLaunch",
                        result.Release.Version),
                    Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational);
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Automatic update check failed: {exception}");
        }
    }

    private void OnActivated(object? sender, AppActivationArguments args)
    {
        if (_window is null)
        {
            // A second launch can arrive while the first launch is still
            // opening the database.  Remember it so the eventual window is
            // focused instead of losing the activation request.
            _activationPending = true;
            return;
        }
        var window = _window;
        window.DispatcherQueue.TryEnqueue(() =>
        {
            window.BringToFront();
        });
    }
}
