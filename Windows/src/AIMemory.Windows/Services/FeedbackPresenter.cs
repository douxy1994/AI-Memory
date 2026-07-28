using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Controls;

namespace AIMemory.Windows.Services;

public static class FeedbackPresenter
{
    private sealed class State
    {
        public CancellationTokenSource? AutoClose { get; set; }
    }

    private static readonly ConditionalWeakTable<InfoBar, State> States = new();

    public static void Show(
        InfoBar bar,
        string message,
        InfoBarSeverity severity,
        string? title = null)
    {
        var state = States.GetOrCreateValue(bar);
        state.AutoClose?.Cancel();
        state.AutoClose?.Dispose();
        state.AutoClose = null;

        bar.Title = title ?? "";
        bar.Message = message;
        bar.Severity = severity;
        bar.IsOpen = true;

        if (severity != InfoBarSeverity.Success) return;
        state.AutoClose = new CancellationTokenSource();
        _ = CloseAfterDelayAsync(bar, state.AutoClose.Token);
    }

    private static async Task CloseAfterDelayAsync(
        InfoBar bar,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            bar.DispatcherQueue.TryEnqueue(() => bar.IsOpen = false);
        }
        catch (OperationCanceledException)
        {
            // A newer message replaced this success state.
        }
    }
}
