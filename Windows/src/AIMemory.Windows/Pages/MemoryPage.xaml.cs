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
    private readonly HashSet<string> _reviewingCandidateIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _committedCandidateIds = new(StringComparer.Ordinal);
    private long _loadGeneration;
    private long _reviewGeneration;
    private long _repositoryGeneration;
    private bool _batchReviewing;

    private void UpdateCandidateRows()
    {
        CandidateList.ItemsSource = _pendingCandidates
            .Where(value => !_committedCandidateIds.Contains(value.Id))
            .Select(value => new CandidateRow(value, !_batchReviewing && !_reviewingCandidateIds.Contains(value.Id)))
            .ToArray();
        RejectAllCandidatesButton.IsEnabled = !_batchReviewing
            && _reviewingCandidateIds.Count == 0 && _pendingCandidates.Count > 0;
    }

    private bool BeginCandidateReview(string id)
    {
        if (_batchReviewing || _committedCandidateIds.Contains(id) || !_reviewingCandidateIds.Add(id)) return false;
        UpdateCandidateRows();
        return true;
    }

    private void EndCandidateReview(string id)
    {
        _reviewingCandidateIds.Remove(id);
        UpdateCandidateRows();
    }

    private async Task CandidateReviewCommittedAsync(IEnumerable<string> ids, string message)
    {
        foreach (var id in ids) _committedCandidateIds.Add(id);
        ++_reviewGeneration; // Invalidate reads started before the transaction committed.
        _pendingCandidates = _pendingCandidates.Where(value => !_committedCandidateIds.Contains(value.Id)).ToArray();
        UpdateCandidateRows();
        Show(message, InfoBarSeverity.Success);
        try
        {
            await Task.WhenAll(ReloadCandidateReviewAsync(), ReloadRepositoryOptionsAsync());
        }
        catch (Exception exception)
        {
            Show(LocalizationService.Format("CandidateCommittedRefreshFailed", exception.Message), InfoBarSeverity.Warning);
        }
    }

    private async Task ReloadCandidateReviewAsync()
    {
        if (_memory is null) return;
        var generation = ++_reviewGeneration;
        var repoId = SelectedRepositoryId();
        var candidates = Task.Run(() => _memory.ListCandidatesAsync());
        var approved = Task.Run(() => _memory.ListApprovedAsync());
        await Task.WhenAll(candidates, approved);
        if (generation != _reviewGeneration || repoId != SelectedRepositoryId()) return;
        _pendingCandidates = (await candidates).Where(value => !_committedCandidateIds.Contains(value.Id)
            && (repoId is null || value.RepoId == repoId)).ToArray();
        UpdateCandidateRows();
        ApprovedList.ItemsSource = (await approved).Where(value => repoId is null || value.RepoId == repoId).ToArray();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs args)
    {
        ++_loadGeneration;
        ++_reviewGeneration;
        ++_repositoryGeneration;
        base.OnNavigatedFrom(args);
    }

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
        await TryReloadAsync(reloadRepositories: true);
    }

    private async Task TryReloadAsync(bool reloadRepositories = false)
    {
        try
        {
            if (reloadRepositories) await ReloadRepositoryOptionsAsync();
            await ReloadAsync();
        }
        catch (Exception exception)
        {
            Show(LocalizationService.Format("MemoryRefreshFailed", exception.Message), InfoBarSeverity.Error);
        }
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
        var generation = ++_loadGeneration;
        var selectedRepoId = SelectedRepositoryId();
        var reviewTask = ReloadCandidateReviewAsync();
        var checkpointsTask = _recovery.ListCheckpointsAsync();
        var handoffsTask = _recovery.ListHandoffsAsync();
        await Task.WhenAll(
            reviewTask,
            checkpointsTask,
            handoffsTask);
        if (generation != _loadGeneration || selectedRepoId != SelectedRepositoryId()) return;
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
        if (generation != _loadGeneration || selectedRepoId != SelectedRepositoryId()) return;
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
        var generation = ++_repositoryGeneration;
        var repositories = await Task.Run(() => _governance.ListRepositoriesAsync());
        if (generation != _repositoryGeneration || selectedId != SelectedRepositoryId()) return;
        _repositories = repositories;
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
            ++_repositoryGeneration;
            _pendingCandidates = [];
            UpdateCandidateRows();
            ApprovedList.ItemsSource = null;
            await TryReloadAsync();
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs args)
    {
        await TryReloadAsync(reloadRepositories: true);
    }

    private async void ApproveCandidate_Click(object sender, RoutedEventArgs args)
    {
        if (_memory is null
            || sender is not Button { Tag: MemoryCandidateRecord candidate })
        {
            return;
        }
        if (!BeginCandidateReview(candidate.Id)) return;
        try
        {
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
            var editedTitle = title.Text;
            var editedValue = value.Text;
            var editedHint = hint.Text;
            var result = await Task.Run(() => _memory.ApproveCandidateAsync(
                candidate.Id, editedTitle, editedValue, editedHint));
            await CandidateReviewCommittedAsync([candidate.Id],
                LocalizationService.Get(result.Created ? "CandidateApproved" : "CandidateAlreadyApproved"));
        }
        catch (Exception exception)
        {
            Show(
                LocalizationService.Format(
                    "ApprovalFailed",
                    exception.Message),
                InfoBarSeverity.Error);
        }
        finally
        {
            EndCandidateReview(candidate.Id);
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
        var conversations = await _window.Conversations.ListAsync(limit: 5_000, includeTemporary: true);
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
        if (_memory is null || _pendingCandidates.Count == 0 || _batchReviewing || _reviewingCandidateIds.Count > 0) return;
        var candidates = _pendingCandidates.ToArray();
        var repoId = SelectedRepositoryId();
        _batchReviewing = true;
        foreach (var candidate in candidates) _reviewingCandidateIds.Add(candidate.Id);
        UpdateCandidateRows();
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = LocalizationService.Get("RejectAllCandidatesTitle"),
                Content = LocalizationService.Format("RejectAllCandidatesDescription", candidates.Length),
                PrimaryButtonText = LocalizationService.Get("RejectAllCandidatesAction"),
                CloseButtonText = LocalizationService.Get("Cancel"),
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            var count = await Task.Run(() => _memory.ReviewAllPendingAsync("reject", repoId));
            await CandidateReviewCommittedAsync(candidates.Select(value => value.Id),
                LocalizationService.Format("CandidatesRejectedCount", count));
        }
        catch (Exception exception)
        {
            Show(LocalizationService.Format("CandidateUpdateFailed", exception.Message), InfoBarSeverity.Error);
        }
        finally
        {
            _batchReviewing = false;
            foreach (var candidate in candidates) _reviewingCandidateIds.Remove(candidate.Id);
            UpdateCandidateRows();
        }
    }

    private async Task ReviewCandidateAsync(object sender, string action, string success)
    {
        if (_memory is null || sender is not Button { Tag: MemoryCandidateRecord candidate }
            || !BeginCandidateReview(candidate.Id)) return;
        try
        {
            await Task.Run(() => _memory.ReviewCandidateAsync(candidate.Id, action));
            await CandidateReviewCommittedAsync([candidate.Id], success);
        }
        catch (Exception exception)
        {
            Show(LocalizationService.Format("CandidateUpdateFailed", exception.Message), InfoBarSeverity.Error);
        }
        finally
        {
            EndCandidateReview(candidate.Id);
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
                limit: 5_000, includeTemporary: true))
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
    public CandidateRow(MemoryCandidateRecord value, bool canReview = true)
    {
        ValueRecord = value;
        CanReview = canReview;
    }

    public MemoryCandidateRecord ValueRecord { get; }
    public bool CanReview { get; }
    public string Kind => ValueRecord.Kind;
    public string Status => CanReview ? ValueRecord.Status : LocalizationService.Get("CandidateProcessing");
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
