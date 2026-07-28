using AIMemory.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace AIMemory.Windows.Pages;

public sealed partial class MemoryPage : Page
{
    private MainWindow? _window;
    private MemoryGovernanceService? _memory;
    private RecoveryService? _recovery;

    public MemoryPage() => InitializeComponent();

    protected override async void OnNavigatedTo(NavigationEventArgs args)
    {
        _window = (MainWindow)args.Parameter;
        _memory = new MemoryGovernanceService(_window.Database);
        _recovery = new RecoveryService(_window.Database);
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        if (_memory is null || _recovery is null) return;
        CandidateList.ItemsSource = await _memory.ListCandidatesAsync();
        ApprovedList.ItemsSource = await _memory.ListApprovedAsync();
        CheckpointList.ItemsSource = await _recovery.ListCheckpointsAsync();
        HandoffList.ItemsSource = (await _recovery.ListHandoffsAsync())
            .Select(value => new HandoffRow(
                value,
                $"{value.FromAgent} → {value.ToAgent}",
                value.Status != "consumed"))
            .ToArray();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs args) =>
        await ReloadAsync();

    private async void ApproveCandidate_Click(object sender, RoutedEventArgs args)
    {
        if (_memory is null
            || sender is not Button { Tag: MemoryCandidateRecord candidate })
        {
            return;
        }
        var title = new TextBox { Header = "标题", Text = candidate.Summary };
        var value = new TextBox
        {
            Header = "规则内容",
            Text = candidate.Value,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 100,
        };
        var hint = new TextBox { Header = "使用提示" };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(title);
        content.Children.Add(value);
        content.Children.Add(hint);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "批准候选规则",
            Content = content,
            PrimaryButtonText = "批准",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            await _memory.ApproveCandidateAsync(
                candidate.Id, title.Text, value.Text, hint.Text);
            await ReloadAsync();
            Show("候选已批准并写入项目记忆。", InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            Show($"批准失败：{exception.Message}", InfoBarSeverity.Error);
        }
    }

    private async void SnoozeCandidate_Click(object sender, RoutedEventArgs args) =>
        await ReviewCandidateAsync(sender, "snooze", "候选已暂缓。");

    private async void RejectCandidate_Click(object sender, RoutedEventArgs args) =>
        await ReviewCandidateAsync(sender, "reject", "候选已拒绝，证据仍保留。");

    private async Task ReviewCandidateAsync(
        object sender,
        string action,
        string success)
    {
        if (_memory is null
            || sender is not Button { Tag: MemoryCandidateRecord candidate })
        {
            return;
        }
        try
        {
            await _memory.ReviewCandidateAsync(candidate.Id, action);
            await ReloadAsync();
            Show(success, InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            Show($"更新候选失败：{exception.Message}", InfoBarSeverity.Error);
        }
    }

    private async void EditApproved_Click(object sender, RoutedEventArgs args)
    {
        if (_memory is null
            || sender is not Button { Tag: ApprovedMemoryRecord memory })
        {
            return;
        }
        var title = new TextBox { Header = "标题", Text = memory.Title };
        var value = new TextBox
        {
            Header = "规则内容",
            Text = memory.Value,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 100,
        };
        var hint = new TextBox { Header = "使用提示", Text = memory.UsageHint };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(title);
        content.Children.Add(value);
        content.Children.Add(hint);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "编辑已批准规则",
            Content = content,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            await _memory.UpdateApprovedAsync(
                memory.Id, title.Text, value.Text, hint.Text);
            await ReloadAsync();
            Show("规则已更新。", InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            Show($"保存失败：{exception.Message}", InfoBarSeverity.Error);
        }
    }

    private async void ReverifyApproved_Click(object sender, RoutedEventArgs args) =>
        await SetApprovedStateAsync(sender, true);

    private async void RetireApproved_Click(object sender, RoutedEventArgs args) =>
        await SetApprovedStateAsync(sender, false);

    private async Task SetApprovedStateAsync(object sender, bool active)
    {
        if (_memory is null
            || sender is not Button { Tag: ApprovedMemoryRecord memory })
        {
            return;
        }
        try
        {
            await _memory.SetApprovedStateAsync(memory.Id, active);
            await ReloadAsync();
            Show(
                active ? "规则已重新验证。" : "规则已停用。",
                InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            Show($"更新规则失败：{exception.Message}", InfoBarSeverity.Error);
        }
    }

    private async void PromoteCheckpoint_Click(object sender, RoutedEventArgs args)
    {
        if (_recovery is null
            || sender is not Button { Tag: CheckpointRecord checkpoint })
        {
            return;
        }
        var detected = new AgentCatalog().Detect()
            .Where(value => value.IsDetected)
            .ToArray();
        var targets = detected.Length > 0
            ? detected
            : new AgentCatalog().Detect().ToArray();
        var picker = new ComboBox
        {
            Header = "目标 Agent",
            ItemsSource = targets,
            DisplayMemberPath = "Label",
            SelectedIndex = 0,
            MinWidth = 320,
        };
        var profile = new TextBox { Header = "目标配置（可选）" };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(picker);
        content.Children.Add(profile);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "创建交接包",
            Content = content,
            PrimaryButtonText = "创建",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary
            || picker.SelectedItem is not AIMemory.Core.Models.AgentIntegrationStatus target)
        {
            return;
        }
        try
        {
            await _recovery.CreateHandoffAsync(
                checkpoint, target.Id, profile.Text);
            await ReloadAsync();
            Show($"已创建发往 {target.Label} 的交接包。", InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            Show($"创建交接包失败：{exception.Message}", InfoBarSeverity.Error);
        }
    }

    private async void ConsumeHandoff_Click(object sender, RoutedEventArgs args)
    {
        if (_recovery is null
            || sender is not Button { Tag: HandoffRow handoff })
        {
            return;
        }
        try
        {
            await _recovery.MarkHandoffConsumedAsync(handoff.Value.Id);
            await ReloadAsync();
            Show("交接包已标记为已消费。", InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            Show($"更新交接包失败：{exception.Message}", InfoBarSeverity.Error);
        }
    }

    private void Show(string message, InfoBarSeverity severity)
    {
        Feedback.Message = message;
        Feedback.Severity = severity;
        Feedback.IsOpen = true;
    }
}

public sealed record HandoffRow(
    HandoffRecord Value,
    string Route,
    bool CanConsume)
{
    public string CurrentGoal => Value.CurrentGoal;
    public string Status => Value.Status;
}
