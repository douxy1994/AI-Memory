using AIMemory.Core.Models;
using AIMemory.Core.Persistence;
using AIMemory.Core.Services;
using AIMemory.Windows.Pages;
using AIMemory.Windows.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using WinRT.Interop;

namespace AIMemory.Windows;

public sealed partial class MainWindow : Window
{
    public AIMemoryDatabase Database { get; }
    public ConversationRepository Conversations { get; }
    public SettingsStore Settings { get; } = new();
    private readonly AppWindow _appWindow;
    private AboutWindow? _aboutWindow;
    private CancellationTokenSource? _automaticBackup;
    private NotificationAreaService? _notificationArea;
    private bool _isExiting;

    public MainWindow(AIMemoryDatabase database)
    {
        Database = database;
        Conversations = new ConversationRepository(database);
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        Title = "AI Memory";

        var handle = WindowNative.GetWindowHandle(this);
        var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle);
        _appWindow = AppWindow.GetFromWindowId(id);
        _appWindow.Resize(new global::Windows.Graphics.SizeInt32(1180, 760));
        _appWindow.Closing += OnAppWindowClosing;
        Navigation.SelectedItem = Navigation.MenuItems[0];
        Navigate("workbench");
        RegisterAccelerators();

        try
        {
            _notificationArea = new NotificationAreaService(
                handle,
                DispatcherQueue,
                BringToFront,
                () => _ = SyncNowAsync(),
                ExitApplication,
                LocalizationService.Get("TrayOpen"),
                LocalizationService.Get("TraySyncNow"),
                LocalizationService.Get("TrayExit"));
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Notification-area initialization failed: {exception}");
        }

        Closed += (_, _) => Cleanup();
    }

    public void BringToFront()
    {
        _appWindow.Show(true);
        var handle = WindowNative.GetWindowHandle(this);
        NativeMethods.ShowWindow(handle, 9);
        NativeMethods.SetForegroundWindow(handle);
    }

    public void ApplyFontFamily(string preference)
    {
        App.ApplyApplicationFont(preference);
        var font = new FontFamily(
            FontPreferenceService.ResolveWindowsFamily(preference));
        Navigation.FontFamily = font;
        MainMenuBar.FontFamily = font;
    }

    public void ConfigureAutomaticBackup(AppSettings settings)
    {
        _automaticBackup?.Cancel();
        _automaticBackup?.Dispose();
        _automaticBackup = null;
        if (!settings.AutoBackupEnabled) return;

        _automaticBackup = new CancellationTokenSource();
        _ = RunAutomaticBackupAsync(
            TimeSpan.FromMinutes(settings.AutoBackupIntervalMinutes),
            _automaticBackup.Token);
    }

    private async Task RunAutomaticBackupAsync(
        TimeSpan interval,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, cancellationToken);
                await new BackupService(Database)
                    .CreateRecoveryPointDetailedAsync(
                        "automatic",
                        10,
                        cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception)
            {
                DispatcherQueue.TryEnqueue(() =>
                    ShowFeedback(
                        LocalizationService.Format(
                            "AutomaticBackupRunFailed",
                            exception.Message),
                        InfoBarSeverity.Error));
            }
        }
    }

    private void Navigation_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        Navigate(args.IsSettingsSelected
            ? "settings"
            : (args.SelectedItemContainer?.Tag as string ?? "workbench"));
    }

    public void NavigateTo(string tag)
    {
        if (tag == "settings")
        {
            if (ReferenceEquals(Navigation.SelectedItem, Navigation.SettingsItem))
            {
                Navigate(tag);
            }
            else
            {
                Navigation.SelectedItem = Navigation.SettingsItem;
            }
            return;
        }
        var item = Navigation.MenuItems
            .Concat(Navigation.FooterMenuItems)
            .OfType<NavigationViewItem>()
            .FirstOrDefault(value => string.Equals(
                value.Tag as string,
                tag,
                StringComparison.Ordinal));
        if (item is null)
        {
            Navigate(tag);
        }
        else if (ReferenceEquals(Navigation.SelectedItem, item))
        {
            Navigate(tag);
        }
        else
        {
            Navigation.SelectedItem = item;
        }
    }

    private void Navigate(string tag)
    {
        var page = tag switch
        {
            "history" => typeof(HistoryPage),
            "memory" => typeof(MemoryPage),
            "favorites" => typeof(FavoritesPage),
            "trash" => typeof(TrashPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(WorkbenchPage),
        };
        ContentFrame.Navigate(page, this);
    }

    private void RegisterAccelerators()
    {
        AddAccelerator(VirtualKey.Number1, VirtualKeyModifiers.Control,
            () => NavigateTo("workbench"));
        AddAccelerator(VirtualKey.Number2, VirtualKeyModifiers.Control,
            () => NavigateTo("memory"));
        AddAccelerator(VirtualKey.Number3, VirtualKeyModifiers.Control,
            () => NavigateTo("history"));
        AddAccelerator(VirtualKey.R, VirtualKeyModifiers.Control,
            () => _ = RefreshAllSourcesAsync());
        AddAccelerator(VirtualKey.S,
            VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift,
            () => _ = SyncNowAsync());
        AddAccelerator((VirtualKey)188, VirtualKeyModifiers.Control,
            () => NavigateTo("settings"));
        AddAccelerator(VirtualKey.Left, VirtualKeyModifiers.Menu,
            GoBack);
        AddAccelerator(VirtualKey.F1, VirtualKeyModifiers.None,
            () => _ = ShowHelpAsync());
    }

    private void AddAccelerator(
        VirtualKey key,
        VirtualKeyModifiers modifiers,
        Action action)
    {
        var accelerator = new KeyboardAccelerator
        {
            Key = key,
            Modifiers = modifiers,
        };
        accelerator.Invoked += (_, args) =>
        {
            action();
            args.Handled = true;
        };
        RootLayout.KeyboardAccelerators.Add(accelerator);
    }

    private void GoBack()
    {
        if (ContentFrame.CanGoBack) ContentFrame.GoBack();
    }

    public async Task RefreshAllSourcesAsync()
    {
        GlobalProgress.Visibility = Visibility.Visible;
        ShowFeedback(
            LocalizationService.Get("RefreshingAllSources"),
            InfoBarSeverity.Informational);
        try
        {
            var report = await new NativeHistoryImportService(Conversations)
                .ImportAllAsync();
            var details = string.Join(
                LocalizationService.Get("ListSeparator"),
                report.Imported.Select(value => $"{value.Key} {value.Value}"));
            ShowFeedback(
                report.Warnings.Count == 0
                    ? LocalizationService.Format(
                        "SourcesRefreshCompleted",
                        details)
                    : LocalizationService.Format(
                        "SourcesRefreshCompletedWithWarnings",
                        details,
                        report.Warnings.Count),
                report.Warnings.Count == 0
                    ? InfoBarSeverity.Success
                    : InfoBarSeverity.Warning);
            NavigateTo("workbench");
        }
        catch (Exception exception)
        {
            ShowFeedback(LocalizationService.Format(
                    "SourcesRefreshFailed",
                    exception.Message),
                InfoBarSeverity.Error);
        }
        finally
        {
            GlobalProgress.Visibility = Visibility.Collapsed;
        }
    }

    public async Task SyncNowAsync()
    {
        GlobalProgress.Visibility = Visibility.Visible;
        ShowFeedback(
            LocalizationService.Get("SyncingChanges"),
            InfoBarSeverity.Informational);
        try
        {
            var settings = await Settings.LoadAsync();
            SyncProgress result;
            if (settings.Sync.Provider == "local"
                && !string.IsNullOrWhiteSpace(settings.Sync.SyncFolder))
            {
                result = await new LocalFolderSyncService(Conversations)
                    .SyncAsync(settings.Sync.SyncFolder);
            }
            else if (settings.Sync.Provider == "webdav"
                     && !string.IsNullOrWhiteSpace(settings.Sync.WebdavHost))
            {
                var credentials = new Services.CredentialService().Load();
                result = await new WebDavService(Conversations).SyncAsync(
                    WebDavService.BuildCollectionUri(settings.Sync),
                    credentials?.Username ?? settings.Sync.Username,
                    credentials?.Password);
            }
            else
            {
                ShowFeedback(
                    LocalizationService.Get("ConfigureSyncFirst"),
                    InfoBarSeverity.Warning);
                return;
            }
            ShowFeedback(
                LocalizationService.Format(
                    "SyncCompleted",
                    result.Uploaded,
                    result.Downloaded,
                    result.Skipped),
                InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            ShowFeedback(LocalizationService.Format(
                    "SyncFailed",
                    exception.Message),
                InfoBarSeverity.Error);
        }
        finally
        {
            GlobalProgress.Visibility = Visibility.Collapsed;
        }
    }

    public void ShowFeedback(string message, InfoBarSeverity severity)
        => Services.FeedbackPresenter.Show(
            GlobalFeedback,
            message,
            severity);

    private async Task ShowHelpAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = RootLayout.XamlRoot,
            Title = LocalizationService.Get("HelpTitle"),
            Content = LocalizationService.Get("HelpContent"),
            CloseButtonText = LocalizationService.Get("Done"),
        };
        await dialog.ShowAsync();
    }

    private void ShowAbout(bool checkForUpdates)
    {
        if (_aboutWindow is null)
        {
            _aboutWindow = new AboutWindow(Settings);
            _aboutWindow.Closed += (_, _) => _aboutWindow = null;
        }
        _aboutWindow.Activate();
        if (checkForUpdates)
        {
            _ = _aboutWindow.CheckForUpdatesAsync(automaticInstall: true);
        }
    }

    public void OpenAboutAndCheckForUpdates() =>
        ShowAbout(checkForUpdates: true);

    private void OnAppWindowClosing(
        AppWindow sender,
        AppWindowClosingEventArgs args)
    {
        if (_isExiting || _notificationArea is null) return;
        args.Cancel = true;
        sender.Hide();
    }

    private void ExitApplication()
    {
        if (_isExiting) return;
        _isExiting = true;
        Cleanup();
        Application.Current.Exit();
    }

    private void Cleanup()
    {
        _automaticBackup?.Cancel();
        _automaticBackup?.Dispose();
        _automaticBackup = null;
        _notificationArea?.Dispose();
        _notificationArea = null;
    }

    private void WorkbenchMenu_Click(object sender, RoutedEventArgs args) =>
        NavigateTo("workbench");

    private void ReviewMenu_Click(object sender, RoutedEventArgs args) =>
        NavigateTo("memory");

    private void HistoryMenu_Click(object sender, RoutedEventArgs args) =>
        NavigateTo("history");

    private async void SyncMenu_Click(object sender, RoutedEventArgs args) =>
        await SyncNowAsync();

    private async void RefreshSourcesMenu_Click(
        object sender,
        RoutedEventArgs args) =>
        await RefreshAllSourcesAsync();

    private void BackMenu_Click(object sender, RoutedEventArgs args) =>
        GoBack();

    private void SettingsMenu_Click(object sender, RoutedEventArgs args) =>
        NavigateTo("settings");

    private async void HelpMenu_Click(object sender, RoutedEventArgs args) =>
        await ShowHelpAsync();

    private void AboutMenu_Click(object sender, RoutedEventArgs args) =>
        ShowAbout(checkForUpdates: false);

    private void CheckUpdatesMenu_Click(object sender, RoutedEventArgs args) =>
        ShowAbout(checkForUpdates: true);
}

file static class NativeMethods
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(
        System.Runtime.InteropServices.UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(nint hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern bool ShowWindow(nint hWnd, int nCmdShow);
}
