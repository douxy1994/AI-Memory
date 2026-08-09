// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
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
using Microsoft.Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace AIMemory.Windows;

public sealed partial class MainWindow : Window
{
    public AIMemoryDatabase Database { get; }
    public ConversationRepository Conversations { get; }
    public SettingsStore Settings { get; } = new();
    public Microsoft.UI.WindowId WindowId => _appWindow.Id;
    public nint NativeHandle => _windowHandle;
    private readonly AppWindow _appWindow;
    private readonly nint _windowHandle;
    private CancellationTokenSource? _automaticBackup;
    private NotificationAreaService? _notificationArea;
    private AboutWindow? _aboutWindow;
    private bool _isExiting;
    private bool _isStartupComplete;
    private readonly SemaphoreSlim _sourceRefreshGate = new(1, 1);

    public MainWindow(AIMemoryDatabase database)
    {
        Database = database;
        Conversations = new ConversationRepository(database);
        InitializeComponent();
        try
        {
            SystemBackdrop = new MicaBackdrop
            {
                Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.Base,
            };
        }
        catch (Exception exception)
        {
            // Windows 10 or a transparency-disabled session falls back to the
            // existing theme brushes without blocking startup.
            StartupDiagnostics.Write("mica.unavailable", exception);
        }
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        Title = "AI Memory";

        _windowHandle = WindowNative.GetWindowHandle(this);
        var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_windowHandle);
        _appWindow = AppWindow.GetFromWindowId(id);
        var iconPath = Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "AppIcon.ico");
        if (File.Exists(iconPath))
        {
            _appWindow.SetIcon(iconPath);
        }
        var scale = Math.Max(
            1d,
            NativeMethods.GetDpiForWindow(_windowHandle) / 96d);
        _appWindow.Resize(new global::Windows.Graphics.SizeInt32(
            (int)Math.Round(1180 * scale),
            (int)Math.Round(760 * scale)));
        _appWindow.Closing += OnAppWindowClosing;
        Sidebar.Attach(this);
        ContentFrame.Navigated += ContentFrame_Navigated;
        RegisterAccelerators();

        try
        {
            _notificationArea = new NotificationAreaService(
                _windowHandle,
                DispatcherQueue,
                BringToFront,
                () => _ = SyncNowAsync(),
                ExitApplication,
                LocalizationService.Get("TrayOpen"),
                LocalizationService.Get("TraySyncNow"),
                LocalizationService.Get("TrayExit"));
            StartupDiagnostics.Write("notification-area.ready");
        }
        catch (Exception exception)
        {
            StartupDiagnostics.Write("notification-area.failed", exception);
            System.Diagnostics.Debug.WriteLine(
                $"Notification-area initialization failed: {exception}");
        }

        Closed += (_, _) => Cleanup();
    }

    /// <summary>
    /// Completes shell initialization after the window is already visible.
    /// Database migration must not make startup look like a failed launch.
    /// </summary>
    public void CompleteStartup(AppSettings settings)
    {
        ApplyFontFamily(settings.FontFamily);
        _isStartupComplete = true;
        StartupProgress.IsActive = false;
        StartupOverlay.Visibility = Visibility.Collapsed;
        Navigate("workbench");
        _ = Sidebar.ReloadAsync();
    }

    public void ShowStartupFailure(Exception exception)
    {
        StartupProgress.IsActive = false;
        StartupStatusText.Text = LocalizationService.Format(
            "StartupFailed",
            exception.Message);
        StartupStatusText.Opacity = 1;
    }

    public void BringToFront()
    {
        _appWindow.Show(true);
        var handle = _windowHandle;
        // AppWindow.Show restores the AppWindow state, while the Win32 calls
        // cover an unpackaged launch whose HWND was created hidden during the
        // first dispatcher turn.
        NativeMethods.ShowWindow(handle, 5);
        NativeMethods.ShowWindow(handle, 9);
        NativeMethods.UpdateWindow(handle);
        NativeMethods.SetForegroundWindow(handle);
        // The first frame can size the HWND before the XamlRoot scale is
        // known; normalize the effective window size after activation.
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            NormalizeInitialWindowSize);
    }

    private bool _initialWindowSizeNormalized;

    /// <summary>
    /// The constructor resizes the AppWindow before the XamlRoot scale
    /// exists, so on scaled displays the HWND can stay physically 1180x760
    /// while the XamlRoot later applies (for example) 1.5x, leaving far
    /// less effective space than intended and breaking content layout.
    /// Re-assert the intended effective size once the real scale is known.
    /// </summary>
    private void NormalizeInitialWindowSize()
    {
        if (_initialWindowSizeNormalized) return;
        _initialWindowSizeNormalized = true;
        var scale = Content.XamlRoot?.RasterizationScale ?? 1.0;
        if (scale <= 0) scale = 1.0;
        if (!NativeMethods.GetWindowRect(_windowHandle, out var rect)) return;
        NativeMethods.SetWindowPos(
            _windowHandle,
            0,
            rect.Left,
            rect.Top,
            (int)(1180 * scale),
            (int)(760 * scale),
            NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
    }

    public void ApplyFontFamily(string preference)
    {
        App.ApplyApplicationFont(preference);
        var font = new FontFamily(
            FontPreferenceService.ResolveWindowsFamily(preference));
        Sidebar.FontFamily = font;
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

    public void NavigateTo(string tag)
    {
        if (!_isStartupComplete) return;
        Navigate(tag);
    }

    /// <summary>
    /// Opens the settings surface at a concrete category.  This keeps Help
    /// destinations actionable instead of leaving users on the generic
    /// settings landing section.
    /// </summary>
    public void OpenSettingsCategory(string category)
    {
        if (!_isStartupComplete) return;
        ContentFrame.Navigate(
            typeof(SettingsPage),
            new SettingsNavigation(this, category));
    }

    public void OpenConversationFromSidebar(ConversationSummary conversation)
    {
        if (!_isStartupComplete) return;
        ContentFrame.Navigate(
            typeof(Pages.ConversationPage),
            new Pages.ConversationNavigation(this, conversation));
    }

    public async Task OpenMachineGroupManagerAsync()
    {
        if (!_isStartupComplete) return;
        Pages.WorkbenchPage? workbench =
            ContentFrame.Content as Pages.WorkbenchPage;
        if (workbench is null)
        {
            NavigateTo("workbench");
            await Task.Yield();
            workbench = ContentFrame.Content as Pages.WorkbenchPage;
        }
        if (workbench is not null)
        {
            await workbench.OpenMachineGroupManagerAsync();
        }
    }

    private void Navigate(string tag)
    {
        if (!_isStartupComplete) return;
        var page = tag switch
        {
            "history" => typeof(HistoryPage),
            "memory" => typeof(MemoryPage),
            "favorites" => typeof(FavoritesPage),
            "trash" => typeof(TrashPage),
            "settings" => typeof(SettingsPage),
            "help" => typeof(HelpPage),
            _ => typeof(WorkbenchPage),
        };
        ContentFrame.Navigate(page, this);
    }

    private void ContentFrame_Navigated(
        object sender,
        Microsoft.UI.Xaml.Navigation.NavigationEventArgs args)
    {
        var settings = args.SourcePageType == typeof(SettingsPage);
        var workbench = args.SourcePageType == typeof(WorkbenchPage);
        ApplyWorkspaceChrome(settings);
        BackToWorkbenchButton.Visibility = workbench
            ? Visibility.Collapsed
            : Visibility.Visible;
        BackToWorkbenchButton.IsHitTestVisible = !workbench;
        if (!settings)
        {
            _ = Sidebar.ReloadAsync();
        }
    }

    private void ApplyWorkspaceChrome(bool settings)
    {
        Sidebar.Visibility = settings
            ? Visibility.Collapsed
            : Visibility.Visible;
        Grid.SetColumn(ContentFrame, settings ? 0 : 1);
        Grid.SetColumnSpan(ContentFrame, settings ? 2 : 1);
    }

    private void RegisterAccelerators()
    {
        AddAccelerator(VirtualKey.Number1, VirtualKeyModifiers.Control,
            () => NavigateTo("workbench"));
        AddAccelerator(VirtualKey.Number2, VirtualKeyModifiers.Control,
            () => NavigateTo("memory"));
        AddAccelerator(VirtualKey.Number3, VirtualKeyModifiers.Control,
            () => NavigateTo("history"));
        AddAccelerator(VirtualKey.I, VirtualKeyModifiers.Control,
            () => _ = ImportChatMemAsync());
        AddAccelerator(VirtualKey.W, VirtualKeyModifiers.Control,
            Close);
        AddAccelerator(VirtualKey.M,
            VirtualKeyModifiers.Control | VirtualKeyModifiers.Menu,
            () => NavigateTo("memory"));
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
            ShowHelp);
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

    public Task SynchronizeInstalledAgentHistoryAfterLaunchAsync() =>
        SynchronizeInstalledAgentHistoryAsync(startup: true);

    public Task RefreshAllSourcesAsync() =>
        SynchronizeInstalledAgentHistoryAsync(startup: false);

    private async Task SynchronizeInstalledAgentHistoryAsync(bool startup)
    {
        if (!_isStartupComplete) return;
        BeginGlobalSyncProgress(LocalizationService.Get("RefreshingAllSources"));
        ShowFeedback(
            LocalizationService.Get("RefreshingAllSources"),
            InfoBarSeverity.Informational);
        try
        {
            await _sourceRefreshGate.WaitAsync();
            var report = await Task.Run(() =>
                new NativeHistoryImportService(Conversations).ImportAllAsync());
            var details = string.Join(
                LocalizationService.Get("ListSeparator"),
                report.Imported.Select(value => $"{value.Key} {value.Value}"));
            ShowFeedback(
                startup
                    ? report.Warnings.Count == 0
                        ? LocalizationService.Format(
                            "SourcesStartupCompleted",
                            report.Imported.Count,
                            report.Total)
                        : LocalizationService.Format(
                            "SourcesStartupCompletedWithWarnings",
                            report.Imported.Count,
                            report.Total,
                            report.Warnings.Count)
                    : report.Warnings.Count == 0
                        ? LocalizationService.Format(
                            "SourcesRefreshCompleted",
                            details)
                        : LocalizationService.Format(
                            "SourcesRefreshCompletedWithWarnings",
                            details,
                            report.Warnings.Count),
                report.Warnings.Count == 0
                    ? startup
                        ? InfoBarSeverity.Informational
                        : InfoBarSeverity.Success
                    : InfoBarSeverity.Warning);
            await Sidebar.ReloadAsync();
            if (ContentFrame.Content is WorkbenchPage workbench)
            {
                await workbench.ReloadAsync();
            }
            StartupDiagnostics.Write(
                $"native-history.complete sources={report.Imported.Count} "
                + $"conversations={report.Total} warnings={report.Warnings.Count}");
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
            if (_sourceRefreshGate.CurrentCount == 0)
            {
                _sourceRefreshGate.Release();
            }
            EndGlobalSyncProgress();
        }
    }

    public async Task SyncNowAsync()
    {
        if (!_isStartupComplete) return;
        BeginGlobalSyncProgress(LocalizationService.Get("SyncProgressPreparing"));
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
                var service = new LocalFolderSyncService(Conversations);
                var progress = new Progress<SyncProgress>(
                    ApplyGlobalSyncProgress);
                result = await Task.Run(() => service.SyncAsync(
                        settings.Sync.SyncFolder,
                        progress: progress));
            }
            else if (settings.Sync.Provider == "webdav"
                     && !string.IsNullOrWhiteSpace(settings.Sync.WebdavHost))
            {
                var credentials = new Services.CredentialService().Load();
                var service = new WebDavService(Conversations);
                var collection = WebDavService.BuildCollectionUri(settings.Sync);
                var username = credentials?.Username ?? settings.Sync.Username;
                var password = credentials?.Password;
                var progress = new Progress<SyncProgress>(
                    ApplyGlobalSyncProgress);
                result = await Task.Run(() => service.SyncAsync(
                    collection, username, password, progress));
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
            EndGlobalSyncProgress();
        }
    }

    private void BeginGlobalSyncProgress(string message)
    {
        GlobalProgress.Visibility = Visibility.Visible;
        GlobalProgressText.Text = message;
        GlobalProgressText.Visibility = Visibility.Visible;
    }

    private void ApplyGlobalSyncProgress(SyncProgress progress)
    {
        GlobalProgressText.Text = string.IsNullOrWhiteSpace(progress.CurrentAgent)
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

    private void EndGlobalSyncProgress()
    {
        GlobalProgress.Visibility = Visibility.Collapsed;
        GlobalProgressText.Visibility = Visibility.Collapsed;
        GlobalProgressText.Text = "";
    }

    public void ShowFeedback(string message, InfoBarSeverity severity)
        => Services.FeedbackPresenter.Show(
            GlobalFeedback,
            message,
            severity);

    public async Task ImportChatMemAsync()
    {
        if (!_isStartupComplete) return;
        string source;
        try
        {
            var picker = new FileOpenPicker(WindowId)
            {
                Title = LocalizationService.Get("ChooseChatMemDatabaseTitle"),
                CommitButtonText =
                    LocalizationService.Get("ChooseChatMemDatabaseCommit"),
                SettingsIdentifier = "AIMemory.ChatMemDatabase",
            };
            picker.FileTypeFilter.Add(".db");
            picker.FileTypeFilter.Add(".sqlite");
            picker.FileTypeFilter.Add(".sqlite3");
            var selected = await picker.PickSingleFileAsync();
            if (selected is null) return;
            source = selected.Path;
        }
        catch (Exception exception)
        {
            ShowFeedback(
                LocalizationService.Format(
                    "ChooseChatMemDatabaseFailed",
                    exception.Message),
                InfoBarSeverity.Error);
            return;
        }

        var confirmation = new ContentDialog
        {
            XamlRoot = RootLayout.XamlRoot,
            Title = LocalizationService.Get("ChatMemImportConfirmTitle"),
            Content = LocalizationService.Format(
                "ChatMemImportConfirmBody",
                source),
            PrimaryButtonText =
                LocalizationService.Get("ChatMemImportConfirmAction"),
            CloseButtonText = LocalizationService.Get("Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        BeginGlobalSyncProgress(
            LocalizationService.Get("ChatMemImportInProgress"));
        ShowFeedback(
            LocalizationService.Get("ChatMemImportInProgress"),
            InfoBarSeverity.Informational);
        try
        {
            var result = await new ChatMemImportService(Database)
                .ImportAsync(source);
            ShowFeedback(
                string.IsNullOrWhiteSpace(result.BackupPath)
                    ? LocalizationService.Get("ChatMemImportCompleted")
                    : LocalizationService.Format(
                        "ChatMemImportCompletedWithBackup",
                        result.BackupPath),
                InfoBarSeverity.Success);
            NavigateTo("workbench");
        }
        catch (Exception exception)
        {
            ShowFeedback(
                LocalizationService.Format(
                    "ImportFailed",
                    exception.Message),
                InfoBarSeverity.Error);
        }
        finally
        {
            EndGlobalSyncProgress();
        }
    }

    private void ShowHelp() => NavigateTo("help");

    public void OpenAboutAndCheckForUpdates() => OpenAbout(checkForUpdates: true);

    private void OpenAbout(bool checkForUpdates = false)
    {
        if (_aboutWindow is null)
        {
            _aboutWindow = new AboutWindow(Database, Settings);
            _aboutWindow.Closed += (_, _) => _aboutWindow = null;
        }
        _aboutWindow.Activate();
        if (checkForUpdates)
        {
            _ = _aboutWindow.CheckForUpdatesAsync(automaticInstall: true);
        }
    }

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

    private void MemoryMenu_Click(object sender, RoutedEventArgs args) =>
        NavigateTo("memory");

    private async void ImportChatMemMenu_Click(
        object sender,
        RoutedEventArgs args) =>
        await ImportChatMemAsync();

    private void CloseWindowMenu_Click(
        object sender,
        RoutedEventArgs args) =>
        Close();

    private async void SyncMenu_Click(object sender, RoutedEventArgs args) =>
        await SyncNowAsync();

    private async void RefreshSourcesMenu_Click(
        object sender,
        RoutedEventArgs args) =>
        await RefreshAllSourcesAsync();

    private void BackMenu_Click(object sender, RoutedEventArgs args) =>
        GoBack();

    private void ReturnWorkbench_Click(object sender, RoutedEventArgs args) =>
        NavigateTo("workbench");

    private void SettingsMenu_Click(object sender, RoutedEventArgs args) =>
        NavigateTo("settings");

    private void HelpMenu_Click(object sender, RoutedEventArgs args) =>
        ShowHelp();

    private void AboutMenu_Click(object sender, RoutedEventArgs args) =>
        OpenAbout();

    private void CheckUpdatesMenu_Click(object sender, RoutedEventArgs args) =>
        OpenAbout(checkForUpdates: true);
}

file static class NativeMethods
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(
        System.Runtime.InteropServices.UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(nint hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(nint hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(
        System.Runtime.InteropServices.UnmanagedType.Bool)]
    internal static extern bool UpdateWindow(nint hWnd);

    internal const uint SwpNoZOrder = 0x0004;
    internal const uint SwpNoActivate = 0x0010;

    [System.Runtime.InteropServices.StructLayout(
        System.Runtime.InteropServices.LayoutKind.Sequential)]
    internal struct WindowRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(
        System.Runtime.InteropServices.UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(
        nint hWnd,
        out WindowRect rect);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(
        System.Runtime.InteropServices.UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(
        nint hWnd,
        nint insertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);
}
