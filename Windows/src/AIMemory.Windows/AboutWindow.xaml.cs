using AIMemory.Core.Persistence;
using AIMemory.Core.Services;
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
        Title = "关于 AI Memory";
        ReleaseVersionText.Text = $"正式版本 {CurrentVersion()}";
        DevelopmentVersionText.Text = $"开发版本 {DevelopmentVersion()}";
        ReleaseTagText.Text = $"v{CurrentVersion()}";
        AgentCoverageText.Text =
            $"✓ Agent 覆盖扩展\n支持 {AgentCatalog.All.Count} 种主流 Agent 与 CLI 检测，已安装项目优先显示。";

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
        CheckUpdateLabel.Text = "正在检查…";
        UpdateStatus.IsOpen = true;
        UpdateStatus.Severity = InfoBarSeverity.Informational;
        UpdateStatus.Message = "正在检查 GitHub Releases…";
        try
        {
            var settings = await _settings.LoadAsync();
            var result = await new UpdateService().CheckAsync(
                settings.UpdateFeedUrl,
                CurrentVersion());
            if (!result.IsUpdateAvailable)
            {
                UpdateStatus.Severity = InfoBarSeverity.Success;
                UpdateStatus.Message =
                    $"当前版本已是最新；更新源最新版本为 {result.Release.Version}。";
                return;
            }

            UpdateStatus.Severity = InfoBarSeverity.Informational;
            UpdateStatus.Message =
                $"发现新版本 {result.Release.Version}：{result.Release.Title}";
            if (!automaticInstall) return;

            UpdateStatus.Message =
                $"发现新版本 {result.Release.Version}，正在下载安装包…";
            var path = await new UpdateService().DownloadAsync(
                result.Release,
                DataPaths.UpdateDirectory);
            var file = await StorageFile.GetFileFromPathAsync(path);
            if (!await Launcher.LaunchFileAsync(file))
            {
                throw new InvalidOperationException("Windows 无法打开安装包。");
            }
            UpdateStatus.Severity = InfoBarSeverity.Success;
            UpdateStatus.Message = "安装包已下载并交给 Windows 安装器。";
        }
        catch (Exception exception)
        {
            UpdateStatus.Severity = InfoBarSeverity.Error;
            UpdateStatus.Message = $"检查更新失败：{exception.Message}";
        }
        finally
        {
            _checking = false;
            CheckUpdateButton.IsEnabled = true;
            CheckUpdateLabel.Text = "检查更新";
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
                ? "未提交构建"
                : revision;
        }
        catch
        {
            return "未提交构建";
        }
    }
}
