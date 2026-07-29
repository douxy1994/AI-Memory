using AIMemory.Core.Models;
using AIMemory.Core.Persistence;
using AIMemory.Core.Services;
using AIMemory.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel;
using Windows.ApplicationModel.DataTransfer;
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
    private bool _loading;

    public SettingsPage() => InitializeComponent();

    protected override async void OnNavigatedTo(NavigationEventArgs args)
    {
        _window = (MainWindow)args.Parameter;
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
            AutoUpdateToggle.IsOn = _settings.AutoCheckUpdates;
            UpdateFeedBox.Text = _settings.UpdateFeedUrl;
            AutoBackupToggle.IsOn = _settings.AutoBackupEnabled;
            AutoBackupIntervalBox.Value =
                _settings.AutoBackupIntervalMinutes;
            if (_credentials.Load() is { } stored)
            {
                UsernameBox.Text = stored.Username;
                PasswordBox.Password = stored.Password;
            }
            await ReloadStartupAsync();
            ReloadAgents();
            DataPathText.Text = $"数据目录：{DataPaths.SupportDirectory}";
            var importer = new ChatMemImportService(_window.Database);
            ImportChatMemButton.IsEnabled = importer.FindSource() is not null;
            await RefreshDiagnosticsAsync();
            if (SettingsCategories.SelectedIndex < 0)
            {
                SettingsCategories.SelectedIndex = 0;
            }
            ShowCategory(
                (SettingsCategories.SelectedItem as ListViewItem)?.Tag as string
                ?? "general");
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
        if (SettingsCategories.SelectedItem is ListViewItem item)
        {
            ShowCategory(item.Tag as string ?? "general");
        }
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
        UpdatesPanel.Visibility = category == "updates"
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async Task ReloadStartupAsync()
    {
        var state = await _startup.GetStateAsync();
        StartupToggle.IsOn = state == StartupTaskState.Enabled;
        StartupDetail.Text = state switch
        {
            StartupTaskState.Enabled => "已开启",
            StartupTaskState.DisabledByUser => "已被系统或用户禁用，可在 Windows 设置中恢复",
            StartupTaskState.DisabledByPolicy => "组织策略禁止开机启动",
            _ => "已关闭",
        };
        StartupToggle.IsEnabled = state != StartupTaskState.DisabledByPolicy;
    }

    private async void StartupToggle_Toggled(object sender, RoutedEventArgs args)
    {
        if (_loading) return;
        try
        {
            await _startup.SetEnabledAsync(StartupToggle.IsOn);
            await ReloadStartupAsync();
            Show("启动设置已更新。", InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            Show($"更新启动设置失败：{exception.Message}", InfoBarSeverity.Error);
        }
    }

    private async void SaveGeneralSettings_Click(
        object sender,
        RoutedEventArgs args)
    {
        if (_window is null) return;
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
                _settings.AutoBackupEnabled
                    ? $"自动备份已开启，每 {_settings.AutoBackupIntervalMinutes} 分钟检查一次变化。"
                    : "自动备份已关闭。",
                InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            Show($"保存自动备份设置失败：{exception.Message}",
                InfoBarSeverity.Error);
        }
    }

    private void DetectAgents_Click(object sender, RoutedEventArgs args) =>
        ReloadAgents();

    private void ReloadAgents() =>
        AgentList.ItemsSource = _agentIntegrations.Detect();

    private void ToggleAgent_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not Button
            {
                Tag: AgentIntegrationStatus integration,
            })
        {
            return;
        }
        try
        {
            _agentIntegrations.SetEnabled(
                integration,
                !integration.IsIntegrated);
            ReloadAgents();
            Show(
                integration.IsIntegrated
                    ? $"{integration.Label} 集成已关闭。"
                    : $"{integration.Label} 集成已启用。",
                InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            Show($"更新 Agent 集成失败：{exception.Message}", InfoBarSeverity.Error);
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
            Show("设置已保存。", InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            Show($"保存失败：{exception.Message}", InfoBarSeverity.Error);
        }
    }

    private async void VerifyWebDav_Click(object sender, RoutedEventArgs args)
    {
        ApplyForm();
        SyncProgress.Visibility = Visibility.Visible;
        try
        {
            var status = await new WebDavService().VerifyAsync(
                WebDavService.BuildCollectionUri(_settings.Sync),
                UsernameBox.Text.Trim(),
                PasswordBox.Password);
            Show($"连接验证成功（HTTP {status}）。", InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            Show($"连接验证失败：{exception.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            SyncProgress.Visibility = Visibility.Collapsed;
        }
    }

    private async void SyncWebDav_Click(object sender, RoutedEventArgs args)
    {
        if (_window is null) return;
        ApplyForm();
        SyncProgress.Visibility = Visibility.Visible;
        try
        {
            var service = new WebDavService(_window.Conversations);
            var result = await service.SyncAsync(
                WebDavService.BuildCollectionUri(_settings.Sync),
                UsernameBox.Text.Trim(),
                PasswordBox.Password);
            Show(result.Message, InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            Show($"同步失败：{exception.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            SyncProgress.Visibility = Visibility.Collapsed;
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
                    ? $"恢复点已创建：{result.Path}"
                    : $"数据没有变化，已保留现有恢复点：{result.Path}",
                InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            Show($"备份失败：{exception.Message}", InfoBarSeverity.Error);
        }
    }

    private async void RestoreBackup_Click(object sender, RoutedEventArgs args)
    {
        if (_window is null) return;
        var service = new BackupService(_window.Database);
        var recoveryPoints = service.ListRecoveryPoints();
        if (recoveryPoints.Count == 0)
        {
            Show("没有可用的恢复点。", InfoBarSeverity.Warning);
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
            Text = "恢复前会自动备份当前数据库。请选择要恢复的时间点：",
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(picker);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "从恢复点恢复",
            Content = content,
            PrimaryButtonText = "恢复",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary
            || picker.SelectedItem is not string selected)
        {
            return;
        }

        try
        {
            SyncProgress.Visibility = Visibility.Visible;
            var safetyBackup = await service.RestoreRecoveryPointAsync(selected);
            await ReloadRestoredSettingsAsync();
            Show(
                $"恢复完成；恢复前的数据已备份到：{safetyBackup}",
                InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            Show($"恢复失败：{exception.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            SyncProgress.Visibility = Visibility.Collapsed;
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
        AutoUpdateToggle.IsOn = _settings.AutoCheckUpdates;
        UpdateFeedBox.Text = _settings.UpdateFeedUrl;
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

    private async void SaveLocalFolder_Click(object sender, RoutedEventArgs args)
    {
        if (_window is null) return;
        _settings.Sync.Provider = "local";
        _settings.Sync.SyncFolder = SyncFolderBox.Text.Trim();
        await _window.Settings.SaveAsync(_settings);
        Show("本地同步目录已保存。", InfoBarSeverity.Success);
    }

    private async void SyncLocalFolder_Click(object sender, RoutedEventArgs args)
    {
        if (_window is null) return;
        try
        {
            var result = await new LocalFolderSyncService(_window.Conversations)
                .SyncAsync(SyncFolderBox.Text.Trim());
            Show(result.Message, InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            Show($"本地同步失败：{exception.Message}", InfoBarSeverity.Error);
        }
    }

    private async void ImportChatMem_Click(object sender, RoutedEventArgs args)
    {
        if (_window is null) return;
        var importer = new ChatMemImportService(_window.Database);
        var source = importer.FindSource();
        if (source is null)
        {
            Show("没有找到可导入的 ChatMem 数据库。", InfoBarSeverity.Warning);
            return;
        }
        try
        {
            var backup = await importer.ImportAsync(source);
            Show(
                string.IsNullOrWhiteSpace(backup)
                    ? "ChatMem 数据已导入。"
                    : $"ChatMem 数据已导入，原数据备份：{backup}",
                InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            Show($"导入失败：{exception.Message}", InfoBarSeverity.Error);
        }
    }

    private async void ImportNativeHistory_Click(object sender, RoutedEventArgs args)
    {
        if (_window is null) return;
        SyncProgress.Visibility = Visibility.Visible;
        try
        {
            var report = await new NativeHistoryImportService(
                _window.Conversations).ImportAllAsync();
            var details = string.Join(
                "，",
                report.Imported.Select(value => $"{value.Key} {value.Value}"));
            Show(
                report.Warnings.Count == 0
                    ? $"本机历史导入完成：{details}。"
                    : $"本机历史导入完成：{details}；{report.Warnings.Count} 项警告。",
                report.Warnings.Count == 0
                    ? InfoBarSeverity.Success
                    : InfoBarSeverity.Warning);
        }
        catch (Exception exception)
        {
            Show($"本机历史导入失败：{exception.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            SyncProgress.Visibility = Visibility.Collapsed;
        }
    }

    private async void SaveUpdateSettings_Click(
        object sender,
        RoutedEventArgs args)
    {
        if (_window is null) return;
        _settings.AutoCheckUpdates = AutoUpdateToggle.IsOn;
        _settings.UpdateFeedUrl = UpdateFeedBox.Text.Trim();
        try
        {
            await _window.Settings.SaveAsync(_settings);
            Show("更新设置已保存。", InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            Show($"保存更新设置失败：{exception.Message}", InfoBarSeverity.Error);
        }
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs args)
    {
        if (_window is null) return;
        _settings.AutoCheckUpdates = AutoUpdateToggle.IsOn;
        _settings.UpdateFeedUrl = UpdateFeedBox.Text.Trim();
        try
        {
            await _window.Settings.SaveAsync(_settings);
            UpdateStatusText.Text = "已打开“关于 AI Memory”并开始检查更新。";
            _window.OpenAboutAndCheckForUpdates();
        }
        catch (Exception exception)
        {
            UpdateStatusText.Text = $"保存更新设置失败：{exception.Message}";
            Show(UpdateStatusText.Text, InfoBarSeverity.Error);
        }
    }

    private async void RefreshDiagnostics_Click(
        object sender,
        RoutedEventArgs args) =>
        await RefreshDiagnosticsAsync();

    private async Task RefreshDiagnosticsAsync()
    {
        if (_window is null) return;
        try
        {
            var report = await new DiagnosticsService(_window.Database)
                .CollectAsync(CurrentVersion());
            DiagnosticsBox.Text = report.ToDisplayText();
        }
        catch (Exception exception)
        {
            DiagnosticsBox.Text = $"读取诊断失败：{exception.Message}";
        }
    }

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs args)
    {
        var package = new DataPackage();
        package.SetText(DiagnosticsBox.Text ?? "");
        Clipboard.SetContent(package);
        Clipboard.Flush();
        Show("诊断信息已复制。", InfoBarSeverity.Success);
    }

    private async void OpenDataDirectory_Click(
        object sender,
        RoutedEventArgs args)
        => await OpenDirectoryAsync(
            DataPaths.SupportDirectory,
            "数据目录");

    private async void OpenBackupDirectory_Click(
        object sender,
        RoutedEventArgs args)
        => await OpenDirectoryAsync(
            DataPaths.BackupDirectory,
            "备份目录");

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
                    $"Windows 无法打开{label}。");
            }
        }
        catch (Exception exception)
        {
            Show($"打开{label}失败：{exception.Message}",
                InfoBarSeverity.Error);
        }
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
            return typeof(SettingsPage).Assembly.GetName().Version?
                .ToString(3) ?? "0.1.0";
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
