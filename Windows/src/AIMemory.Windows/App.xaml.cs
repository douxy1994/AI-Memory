using AIMemory.Core.Persistence;
using AIMemory.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.Globalization;

namespace AIMemory.Windows;

public sealed partial class App : Application
{
    private readonly AppInstance _instance;
    private MainWindow? _window;

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
        DataPaths.EnsureDirectories();
        var database = new AIMemoryDatabase();
        await database.InitializeAsync();
        var settings = await new SettingsStore().LoadAsync();
        ApplyApplicationLanguage(settings.Language);
        ApplyApplicationFont(settings.FontFamily);
        _window = new MainWindow(database);
        _window.ApplyFontFamily(settings.FontFamily);
        _window.Activate();
        _window.ConfigureAutomaticBackup(settings);
        _ = CheckForUpdatesAtLaunchAsync();
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
        _window?.DispatcherQueue.TryEnqueue(() =>
        {
            _window.Activate();
            _window.BringToFront();
        });
    }
}
