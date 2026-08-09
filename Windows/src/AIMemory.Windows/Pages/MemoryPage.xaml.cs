// AI Memory
// Copyright © 2026 douxy1994
// SPDX-License-Identifier: AGPL-3.0-only
//
using AIMemory.Core.Services;
using AIMemory.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System.Text.Json;
using Windows.ApplicationModel.DataTransfer;

namespace AIMemory.Windows.Pages;

public sealed partial class MemoryPage : Page
{
    private MainWindow? _window;
    private MemoryGovernanceService? _memory;
    private RecoveryService? _recovery;
    private RepositoryGovernanceService? _governance;
    private KnowledgeProjectionService? _knowledge;
    private IReadOnlyList<RepositorySummary> _repositories = [];
    private IReadOnlyList<MemoryCandidateRecord> _pendingCandidates = [];
    private IReadOnlyList<CheckpointRecord> _checkpoints = [];
    private bool _loadingRepositories;

    public MemoryPage() => InitializeComponent();

    protected override async void OnNavigatedTo(NavigationEventArgs args)
    {
        _window = (MainWindow)args.Parameter;
        _memory = new MemoryGovernanceService(_window.Database);
        _recovery = new RecoveryService(_window.Database);
        _governance = new RepositoryGovernanceService(_window.Database);
        _knowledge = new KnowledgeProjectionService(
            _window.Database,
            _governance);
        await ReloadRepositoryOptionsAsync();
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        if (_memory is null
            || _recovery is null
            || _governance is null
            || _knowledge is null)
        {
            return;
        }
        var candidatesTask = _memory.ListCandidatesAsync();
        var approvedTask = _memory.ListApprovedAsync();
        var checkpointsTask = _recovery.ListCheckpointsAsync();
        var handoffsTask = _recovery.ListHandoffsAsync();
        await Task.WhenAll(
            candidatesTask,
            approvedTask,
            checkpointsTask,
            handoffsTask);
        var selectedRepoId = SelectedRepositoryId();
        _pendingCandidates = (await candidatesTask)
            .Where(value => selectedRepoId is null
                || value.RepoId == selectedRepoId)
            .ToArray();
        CandidateList.ItemsSource = _pendingCandidates
            .Select(value => new CandidateRow(value))
            .ToArray();
        RejectAllCandidatesButton.IsEnabled =
            _pendingCandidates.Count > 0;
        ApprovedList.ItemsSource = (await approvedTask)
            .Where(value => selectedRepoId is null
                || value.RepoId == selectedRepoId)
            .ToArray();
        _checkpoints = (await checkpointsTask)
            .Where(value => selectedRepoId is null
                || value.RepoId == selectedRepoId)
            .ToArray();
        CheckpointList.ItemsSource = _checkpoints;
        HandoffList.ItemsSource = (await handoffsTask)
            .Where(value => selectedRepoId is null
                || value.RepoId == selectedRepoId)
            .Select(value => new HandoffRow(
                value,
                $"{value.FromAgent} → {value.ToAgent}",
                value.Status != "consumed"))
            .ToArray();
        var conflictTasks = _repositories
            .Where(repository => selectedRepoId is null
                || repository.Id == selectedRepoId)
            .Select(repository => _knowledge.ListConflictsAsync(
                repository.Root,
                "open"))
            .ToArray();
        var conflicts = conflictTasks.Length == 0
            ? []
            : (await Task.WhenAll(conflictTasks))
                .SelectMany(value => value)
                .OrderByDescending(value => value.CreatedAt)
                .ToArray();
        ConflictList.ItemsSource = conflicts;
        NoConflictsText.Visibility = conflicts.Length == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async Task ReloadRepositoryOptionsAsync()
    {
        if (_governance is null) return;
        var selectedId =
            (RepositoryBox.SelectedItem as RepositoryOption)?.Id;
        _repositories = await _governance.ListRepositoriesAsync();
        var options = new[]
            {
                new RepositoryOption(
                    null,
                    LocalizationService.Get("AllRepositories")),
            }
            .Concat(_repositories.Select(repository =>
                new RepositoryOption(
                    repository.Id,
                    repository.PendingCandidates == 0
                        ? repository.Root
                        : LocalizationService.Format(
                            "RepositoryPendingCandidates",
                            repository.Root,
                            repository.PendingCandidates))))
            .ToArray();
        _loadingRepositories = true;
        RepositoryBox.ItemsSource = options;
        RepositoryBox.SelectedItem = options.FirstOrDefault(value =>
            value.Id == selectedId)
            ?? options.FirstOrDefault(value => value.Id is not null)
            ?? options[0];
        _loadingRepositories = false;
    }

    private string? SelectedRepositoryId() =>
        (RepositoryBox.SelectedItem as RepositoryOption)?.Id;

    private async void RepositoryBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs args)
    {
        if (!_loadingRepositories)
        {
            await ReloadAsync();
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs args)
    {
        await ReloadRepositoryOptionsAsync();
        await ReloadAsync();
    }

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

    private async void OpenCandidateSource_Click(
        object sender,
        RoutedEventArgs args)
    {
        if (_window is null
            || sender is not FrameworkElement element
            || element.Tag is not CandidateRow row)
        {
            return;
        }
        var reference = row.ValueRecord.Evidence
            .Select(value => value.ConversationId)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (string.IsNullOrWhiteSpace(reference)) return;

        var candidates = HistoryProjectionService.ConversationIdCandidates(
            reference);
        var conversations = await _window.Conversations.ListAsync(limit: 5_000);
        var conversation = conversations.FirstOrDefault(value =>
            candidates.Contains(value.Id, StringComparer.Ordinal)
            || candidates.Contains(
                value.SourceConversationId,
                StringComparer.Ordinal));
        if (conversation is null)
        {
            Show(
                LocalizationService.Get("CandidateSourceNotFound"),
                InfoBarSeverity.Warning);
            return;
        }
        Frame.Navigate(
            typeof(ConversationPage),
            new ConversationNavigation(_window, conversation));
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

    private async void RejectAllCandidates_Click(
        object sender,
        RoutedEventArgs args)
    {
        if (_memory is null || _pendingCandidates.Count == 0) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = LocalizationService.Get("RejectAllCandidatesTitle"),
            Content = LocalizationService.Format(
                "RejectAllCandidatesDescription",
                _pendingCandidates.Count),
            PrimaryButtonText =
                LocalizationService.Get("RejectAllCandidatesAction"),
            CloseButtonText = LocalizationService.Get("Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            var count = await _memory.ReviewAllPendingAsync(
                "reject",
                SelectedRepositoryId());
            await ReloadAsync();
            Show(
                LocalizationService.Format(
                    "CandidatesRejectedCount",
                    count),
                InfoBarSeverity.Success);
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

    private void OpenConflictRules_Click(
        object sender,
        RoutedEventArgs args) =>
        MemoryTabs.SelectedIndex = 1;

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

    private async void ShowHandoffDetails_Click(
        object sender,
        RoutedEventArgs args)
    {
        if (sender is Button { Tag: HandoffRow handoff })
        {
            await ShowHandoffDetailsAsync(handoff);
        }
    }

    private async Task ShowHandoffDetailsAsync(HandoffRow handoff)
    {
        var content = new StackPanel { Spacing = 12 };
        AddHandoffSection(
            content,
            LocalizationService.Get("HandoffCurrentGoal"),
            [handoff.Value.CurrentGoal]);
        AddHandoffSection(
            content,
            LocalizationService.Get("HandoffDone"),
            ParseStringArray(handoff.Value.DoneJson));
        AddHandoffSection(
            content,
            LocalizationService.Get("HandoffNext"),
            ParseStringArray(handoff.Value.NextJson));
        AddHandoffSection(
            content,
            LocalizationService.Get("HandoffKeyFiles"),
            ParseStringArray(handoff.Value.KeyFilesJson),
            monospace: true);
        AddHandoffSection(
            content,
            LocalizationService.Get("HandoffCommands"),
            ParseStringArray(handoff.Value.CommandsJson),
            monospace: true);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = LocalizationService.Get("HandoffDetailsTitle"),
            Content = new ScrollViewer
            {
                Content = content,
                MinWidth = 520,
                MaxHeight = 560,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            },
            PrimaryButtonText = LocalizationService.Get("CopyHandoff"),
            SecondaryButtonText =
                LocalizationService.Get("OpenSourceConversation"),
            CloseButtonText = LocalizationService.Get("Done"),
            DefaultButton = ContentDialogButton.Close,
        };
        switch (await dialog.ShowAsync())
        {
            case ContentDialogResult.Primary:
                CopyText(HandoffText(handoff.Value));
                Show(
                    LocalizationService.Get("HandoffCopied"),
                    InfoBarSeverity.Success);
                break;
            case ContentDialogResult.Secondary:
                await OpenHandoffSourceAsync(handoff);
                break;
        }
    }

    private async void OpenHandoffSource_Click(
        object sender,
        RoutedEventArgs args)
    {
        if (sender is Button { Tag: HandoffRow handoff })
        {
            await OpenHandoffSourceAsync(handoff);
        }
    }

    private async Task OpenHandoffSourceAsync(HandoffRow handoff)
    {
        if (_window is null) return;
        var checkpoint = _checkpoints.FirstOrDefault(value =>
            value.Id == handoff.Value.CheckpointId);
        if (checkpoint is null)
        {
            Show(
                LocalizationService.Get(
                    "HandoffSourceCheckpointUnavailable"),
                InfoBarSeverity.Warning);
            return;
        }
        var candidateIds =
            HistoryProjectionService.ConversationIdCandidates(
                checkpoint.ConversationId,
                checkpoint.SourceAgent);
        var conversation = (await _window.Conversations.ListAsync(
                sourceAgent: checkpoint.SourceAgent,
                limit: 5_000))
            .FirstOrDefault(value => candidateIds.Contains(
                value.Id,
                StringComparer.Ordinal));
        if (conversation is null)
        {
            Show(
                LocalizationService.Get("HistorySourceUnavailable"),
                InfoBarSeverity.Warning);
            return;
        }
        Frame.Navigate(
            typeof(ConversationPage),
            new ConversationNavigation(_window, conversation));
    }

    private static void AddHandoffSection(
        Panel content,
        string title,
        IReadOnlyList<string> values,
        bool monospace = false)
    {
        if (values.Count == 0) return;
        var section = new StackPanel { Spacing = 6 };
        section.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        foreach (var value in values)
        {
            section.Children.Add(new TextBlock
            {
                Text = $"• {value}",
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                FontFamily = monospace
                    ? new Microsoft.UI.Xaml.Media.FontFamily(
                        "Cascadia Mono")
                    : null,
            });
        }
        content.Children.Add(section);
    }

    private static IReadOnlyList<string> ParseStringArray(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static string HandoffText(HandoffRecord handoff)
    {
        var lines = new List<string>
        {
            $"# {handoff.CurrentGoal}",
            "",
            $"{handoff.FromAgent} -> {handoff.ToAgent}",
        };
        Append(
            LocalizationService.Get("HandoffDone"),
            ParseStringArray(handoff.DoneJson));
        Append(
            LocalizationService.Get("HandoffNext"),
            ParseStringArray(handoff.NextJson));
        Append(
            LocalizationService.Get("HandoffKeyFiles"),
            ParseStringArray(handoff.KeyFilesJson));
        Append(
            LocalizationService.Get("HandoffCommands"),
            ParseStringArray(handoff.CommandsJson));
        return string.Join(Environment.NewLine, lines);

        void Append(string title, IReadOnlyList<string> values)
        {
            if (values.Count == 0) return;
            lines.Add("");
            lines.Add($"## {title}");
            lines.AddRange(values.Select(value => $"- {value}"));
        }
    }

    private static void CopyText(string value)
    {
        var package = new DataPackage();
        package.SetText(value);
        Clipboard.SetContent(package);
        Clipboard.Flush();
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

public sealed record RepositoryOption(
    string? Id,
    string Label);

public sealed class CandidateRow
{
    public CandidateRow(MemoryCandidateRecord value)
    {
        ValueRecord = value;
    }

    public MemoryCandidateRecord ValueRecord { get; }
    public string Kind => ValueRecord.Kind;
    public string Status => ValueRecord.Status;
    public string Summary => ValueRecord.Summary;
    public string Value => ValueRecord.Value;
    public string WhyItMatters => ValueRecord.WhyItMatters;
    public IReadOnlyList<string> EvidenceRefs => ValueRecord.EvidenceRefs;
    public string ConfidenceLabel => LocalizationService.Format(
        "CandidateConfidence",
        ValueRecord.Confidence);
    public string MergeSuggestionLabel => LocalizationService.Format(
        "CandidateMergeSuggestion",
        ValueRecord.MergeSuggestion ?? "");
    public string ConflictSuggestionLabel => LocalizationService.Format(
        "CandidateConflictSuggestion",
        ValueRecord.ConflictSuggestion ?? "");
    public Visibility EvidenceVisibility =>
        EvidenceRefs.Count == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
    public Visibility MergeVisibility =>
        string.IsNullOrWhiteSpace(ValueRecord.MergeSuggestion)
            ? Visibility.Collapsed
            : Visibility.Visible;
    public Visibility ConflictVisibility =>
        string.IsNullOrWhiteSpace(ValueRecord.ConflictSuggestion)
            ? Visibility.Collapsed
            : Visibility.Visible;

    public Visibility SourceVisibility =>
        ValueRecord.Evidence.Any(value =>
            !string.IsNullOrWhiteSpace(value.ConversationId))
            ? Visibility.Visible
            : Visibility.Collapsed;
}
