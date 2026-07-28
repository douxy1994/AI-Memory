using AIMemory.Core.Models;
using AIMemory.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace AIMemory.Windows.Pages;

public sealed partial class ConversationPage : Page
{
    private ConversationNavigation? _context;

    public ConversationPage() => InitializeComponent();

    protected override async void OnNavigatedTo(NavigationEventArgs args)
    {
        var context = (ConversationNavigation)args.Parameter;
        _context = context;
        TitleText.Text = string.IsNullOrWhiteSpace(context.Conversation.Summary)
            ? "未命名对话"
            : context.Conversation.Summary;
        MetadataText.Text =
            $"{context.Conversation.SourceAgent} · {context.Conversation.RepoId}";
        MessageList.ItemsSource = await context.Window.Conversations.ReadMessagesAsync(
            context.Conversation.Id);
        var detected = new AgentCatalog().Detect()
            .Where(value => value.IsDetected)
            .ToArray();
        TargetAgentBox.ItemsSource = detected.Length > 0
            ? detected
            : new AgentCatalog().Detect();
        TargetAgentBox.SelectedIndex = 0;
    }

    private void Back_Click(object sender, RoutedEventArgs args)
    {
        if (Frame.CanGoBack) Frame.GoBack();
    }

    private async void CreateCheckpoint_Click(object sender, RoutedEventArgs args)
    {
        if (_context is null) return;
        try
        {
            var messageCount = (await _context.Window.Conversations.ReadMessagesAsync(
                _context.Conversation.Id)).Count;
            await new RecoveryService(_context.Window.Database)
                .CreateCheckpointAsync(_context.Conversation, messageCount);
            Show("检查点已创建。", InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            Show($"创建检查点失败：{exception.Message}", InfoBarSeverity.Error);
        }
    }

    private async void CreateHandoff_Click(object sender, RoutedEventArgs args)
    {
        if (_context is null
            || TargetAgentBox.SelectedItem is not AgentIntegrationStatus target)
        {
            Show("请选择目标 Agent。", InfoBarSeverity.Warning);
            return;
        }
        try
        {
            var messages = await _context.Window.Conversations.ReadMessagesAsync(
                _context.Conversation.Id);
            var service = new RecoveryService(_context.Window.Database);
            var checkpoint = await service.CreateCheckpointAsync(
                _context.Conversation, messages.Count);
            await service.CreateHandoffAsync(checkpoint, target.Id);
            Show($"已创建发往 {target.Label} 的交接包。", InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            Show($"创建交接失败：{exception.Message}", InfoBarSeverity.Error);
        }
    }

    private void Show(string message, InfoBarSeverity severity)
    {
        Feedback.Message = message;
        Feedback.Severity = severity;
        Feedback.IsOpen = true;
    }
}
