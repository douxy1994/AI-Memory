using AIMemory.Core.Services;
using AIMemory.Windows.Services;
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
        var title = new TextBox
        {
            Header = LocalizationService.Get("Title"),
            Text = candidate.Summary,
        };
        var value = new TextBox
        {
            Header = LocalizationService.Get("RuleContent"),
            Text = candidate.Value,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 100,
        };
        var hint = new TextBox
        {
            Header = LocalizationService.Get("UsageHint"),
        };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(title);
        content.Children.Add(value);
        content.Children.Add(hint);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = LocalizationService.Get("ApproveCandidateRule"),
            Content = content,
            PrimaryButtonText = LocalizationService.Get("Approve"),
            CloseButtonText = LocalizationService.Get("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            await _memory.ApproveCandidateAsync(
                candidate.Id, title.Text, value.Text, hint.Text);
            await ReloadAsync();
            Show(
                LocalizationService.Get("CandidateApproved"),
                InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            Show(
                LocalizationService.Format(
                    "ApprovalFailed",
                    exception.Message),
                InfoBarSeverity.Error);
        }
    }

    private async void SnoozeCandidate_Click(object sender, RoutedEventArgs args) =>
        await ReviewCandidateAsync(
            sender,
            "snooze",
            LocalizationService.Get("CandidateSnoozed"));

    private async void RejectCandidate_Click(object sender, RoutedEventArgs args) =>
        await ReviewCandidateAsync(
            sender,
            "reject",
            LocalizationService.Get("CandidateRejected"));

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
            Show(
                LocalizationService.Format(
                    "CandidateUpdateFailed",
                    exception.Message),
                InfoBarSeverity.Error);
        }
    }

    private async void EditApproved_Click(object sender, RoutedEventArgs args)
    {
        if (_memory is null
            || sender is not Button { Tag: ApprovedMemoryRecord memory })
        {
            return;
        }
        var title = new TextBox
        {
            Header = LocalizationService.Get("Title"),
            Text = memory.Title,
        };
        var value = new TextBox
        {
            Header = LocalizationService.Get("RuleContent"),
            Text = memory.Value,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 100,
        };
        var hint = new TextBox
        {
            Header = LocalizationService.Get("UsageHint"),
            Text = memory.UsageHint,
        };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(title);
        content.Children.Add(value);
        content.Children.Add(hint);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = LocalizationService.Get("EditApprovedRule"),
            Content = content,
            PrimaryButtonText = LocalizationService.Get("Save"),
            CloseButtonText = LocalizationService.Get("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            await _memory.UpdateApprovedAsync(
                memory.Id, title.Text, value.Text, hint.Text);
            await ReloadAsync();
            Show(
                LocalizationService.Get("RuleUpdated"),
                InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            Show(
                LocalizationService.Format(
                    "SettingsSaveFailed",
                    exception.Message),
                InfoBarSeverity.Error);
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
                LocalizationService.Get(
                    active ? "RuleReverified" : "RuleDisabled"),
                InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            Show(
                LocalizationService.Format(
                    "RuleUpdateFailed",
                    exception.Message),
                InfoBarSeverity.Error);
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
            Header = LocalizationService.Get("TargetAgent"),
            ItemsSource = targets,
            DisplayMemberPath = "Label",
            SelectedIndex = 0,
            MinWidth = 320,
        };
        var profile = new TextBox
        {
            Header = LocalizationService.Get("TargetProfileOptional"),
        };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(picker);
        content.Children.Add(profile);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = LocalizationService.Get("CreateHandoff"),
            Content = content,
            PrimaryButtonText = LocalizationService.Get("Create"),
            CloseButtonText = LocalizationService.Get("Cancel"),
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
            Show(
                LocalizationService.Format(
                    "HandoffCreated",
                    target.Label),
                InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            Show(
                LocalizationService.Format(
                    "HandoffCreationFailed",
                    exception.Message),
                InfoBarSeverity.Error);
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
            Show(
                LocalizationService.Get("HandoffConsumed"),
                InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            Show(
                LocalizationService.Format(
                    "HandoffUpdateFailed",
                    exception.Message),
                InfoBarSeverity.Error);
        }
    }

    private void Show(string message, InfoBarSeverity severity)
        => AIMemory.Windows.Services.FeedbackPresenter.Show(
            Feedback,
            message,
            severity);
}

public sealed record HandoffRow(
    HandoffRecord Value,
    string Route,
    bool CanConsume)
{
    public string CurrentGoal => Value.CurrentGoal;
    public string Status => Value.Status;
}
