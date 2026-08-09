// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
using System.Runtime.InteropServices;
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
    private int _launchStarted;

    public App(AppInstance instance)
    {
        _instance = instance;
        InitializeComponent();
        _instance.Activated += OnActivated;
        UnhandledException += (_, eventArgs) =>
        {
            var detail = eventArgs.Exception.ToString()
                .Replace("\r\n", " | ")
                .Replace("\n", " | ");
            Services.StartupDiagnostics.Write(
                "app.unhandled " + detail);
            System.Diagnostics.Debug.WriteLine(eventArgs.Exception);
        };

        // Application.Start normally raises OnLaunched after constructing the
        // App.  Starting the shell from the constructor as well keeps direct
        // unpackaged launches deterministic on Windows runner sessions where
        // the activation callback can be delayed until after the dispatcher
        // has already entered its message loop.
        StartLaunch();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        StartLaunch();
    }

    private void StartLaunch()
    {
        if (Interlocked.Exchange(ref _launchStarted, 1) != 0)
        {
            return;
        }
        _ = LaunchAsync();
    }

    private async Task LaunchAsync()
    {
        StartupDiagnostics.Write("launch.begin");
        try
        {
            DataPaths.EnsureDirectories();
            StartupDiagnostics.Write("directories.ready");
            var database = new AIMemoryDatabase();
            var settingsStore = new SettingsStore();
            _window = new MainWindow(database);
            StartupDiagnostics.Write(
                $"window.created hwnd=0x{_window.NativeHandle.ToInt64():X}");
            _window.Activate();
            StartupDiagnostics.Write("window.activate.called");
            // AppWindow.Show(true) is an explicit restore/show operation.  It
            // matters for unpackaged WinUI launches where Activate can create
            // the HWND without making it visible until the dispatcher turns.
            _window.BringToFront();
            StartupDiagnostics.Write("window.bring-to-front.completed");
            StartupDiagnostics.Write(
                $"window.activated hwnd=0x{_window.NativeHandle.ToInt64():X}");
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
            _ = _window.SynchronizeInstalledAgentHistoryAfterLaunchAsync();
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
        var family = FontPreferenceService.ResolveWindowsFamily(preference);
        if (!IsFontInstalled(family))
        {
            // WinUI collapses text layout when the family is missing, so
            // fall back explicitly instead of relying on XAML font lookup.
            family = "Segoe UI Variable Text";
        }
        Current.Resources["ContentControlThemeFontFamily"] =
            new FontFamily(family);
    }

    private static bool IsFontInstalled(string family)
    {
        var found = false;
        var query = new NativeFontLog { CharSet = 1 };
        var hdc = GetDC(nint.Zero);
        try
        {
            EnumFontFamiliesExW(
                hdc,
                ref query,
                (ref NativeFontLog font, nint metrics, uint fontType, nint data) =>
                {
                    if (string.Equals(
                            font.FaceName,
                            family,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        found = true;
                        return 0;
                    }
                    return 1;
                },
                nint.Zero,
                0);
        }
        finally
        {
            ReleaseDC(nint.Zero, hdc);
        }
        return found;
    }

    private delegate int FontEnumProc(
        ref NativeFontLog font,
        nint metrics,
        uint fontType,
        nint data);

    [StructLayout(
        LayoutKind.Sequential,
        CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct NativeFontLog
    {
        public int Height;
        public int Width;
        public int Escapement;
        public int Orientation;
        public int Weight;
        public byte Italic;
        public byte Underline;
        public byte StrikeOut;
        public byte CharSet;
        public byte OutPrecision;
        public byte ClipPrecision;
        public byte Quality;
        public byte PitchAndFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string FaceName;
    }

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern int EnumFontFamiliesExW(
        nint hdc,
        ref NativeFontLog logFont,
        FontEnumProc proc,
        nint lParam,
        uint flags);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint window, nint hdc);

    public static void ApplyApplicationLanguage(string preference)
    {
        var tag = LanguagePreferenceService.ResolveWindowsLanguageTag(preference);
        if (tag.Length == 0)
        {
            // "system" follows Windows.  MRT Core rejects an empty override
            // with 0x80070057, so mirror the top Windows display language
            // instead of assigning "".
            tag = global::Windows.System.UserProfile.GlobalizationPreferences
                .Languages.FirstOrDefault() ?? "";
        }
        if (tag.Length == 0) return;
        ApplicationLanguages.PrimaryLanguageOverride = tag;
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
                .ToString(3) ?? "0.1.3";
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
