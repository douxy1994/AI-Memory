using AIMemory.Core.Persistence;
using AIMemory.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

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
        _window = new MainWindow(database);
        _window.Activate();
        _ = CheckForUpdatesAtLaunchAsync();
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
                    $"发现 AI Memory {result.Release.Version}，请在设置中查看并安装。",
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
