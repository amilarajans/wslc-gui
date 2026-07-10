using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using OrchardWin.Core.Models;
using OrchardWin.Core.Services;

namespace OrchardWin.App.Views;

/// Ported from Orchard's `TestModelPromptView`. Talks to the provider on the host
/// (127.0.0.1), the same endpoint detection uses. Intentionally a proof of concept: no
/// streaming, no persistence - matches the Swift original's own stated scope.
public sealed partial class TestModelPromptDialog : ContentDialog
{
    private readonly ModelService _modelService;
    private readonly ushort _port;
    private readonly ModelApiStyle _api;
    private readonly List<ChatMessage> _messages = [];
    private bool _isSending;

    public TestModelPromptDialog(ModelService modelService, string providerName, ushort port, ModelApiStyle api, string model)
    {
        _modelService = modelService;
        _port = port;
        _api = api;
        InitializeComponent();

        SubtitleText.Text = $"{providerName} · 127.0.0.1:{port}";
        ModelBox.Text = model;
    }

    private void OnNewChatClick(object sender, RoutedEventArgs e)
    {
        if (_isSending) return;
        _messages.Clear();
        TranscriptPanel.Children.Clear();
        EmptyHintText.Visibility = Visibility.Visible;
        TranscriptPanel.Children.Add(EmptyHintText);
    }

    private void OnInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Enter sends; Shift+Enter inserts a newline (TextBox's default AcceptsReturn
        // behavior), mirroring the Swift original's Cmd+Return-to-send / plain-Return-for-
        // newline split as closely as a TextBox's built-in key handling allows.
        if (e.Key == VirtualKey.Enter && !IsShiftPressed())
        {
            e.Handled = true;
            _ = SendAsync();
        }
    }

    private static bool IsShiftPressed()
    {
        var state = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
        return state.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
    }

    private void OnSendClick(object sender, RoutedEventArgs e) => _ = SendAsync();

    private bool CanSend =>
        !_isSending && !string.IsNullOrWhiteSpace(ModelBox.Text) && !string.IsNullOrWhiteSpace(InputBox.Text);

    private async Task SendAsync()
    {
        if (!CanSend) return;

        var text = InputBox.Text.Trim();
        InputBox.Text = "";

        EmptyHintText.Visibility = Visibility.Collapsed;
        var userMessage = new ChatMessage { Role = ChatRole.User, Content = text };
        _messages.Add(userMessage);
        AppendBubble(userMessage);

        _isSending = true;
        SendButton.IsEnabled = false;
        var thinking = AppendThinkingIndicator();

        var model = ModelBox.Text.Trim();
        var history = _messages.ToList();
        try
        {
            var reply = await _modelService.CompleteAsync(_port, _api, model, history);
            TranscriptPanel.Children.Remove(thinking);
            var assistantMessage = new ChatMessage { Role = ChatRole.Assistant, Content = reply };
            _messages.Add(assistantMessage);
            AppendBubble(assistantMessage);
        }
        catch (Exception ex)
        {
            TranscriptPanel.Children.Remove(thinking);
            AppendError(ex.Message);
        }
        finally
        {
            _isSending = false;
            SendButton.IsEnabled = true;
            ScrollToEnd();
        }
    }

    private void AppendBubble(ChatMessage message)
    {
        var isUser = message.Role == ChatRole.User;

        var roleLabel = new TextBlock
        {
            Text = isUser ? "You" : "Assistant",
            Opacity = 0.6,
            FontSize = 11,
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
        };

        var bubble = new Border
        {
            Background = new SolidColorBrush(isUser
                ? Windows.UI.Color.FromArgb(38, 0, 120, 215)
                : Windows.UI.Color.FromArgb(20, 128, 128, 128)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10),
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            MaxWidth = 380,
            Child = new TextBlock
            {
                Text = message.Content,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            },
        };

        var stack = new StackPanel { Spacing = 2, HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left };
        stack.Children.Add(roleLabel);
        stack.Children.Add(bubble);

        TranscriptPanel.Children.Add(stack);
        ScrollToEnd();
    }

    private StackPanel AppendThinkingIndicator()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        panel.Children.Add(new ProgressRing { IsActive = true, Width = 14, Height = 14 });
        panel.Children.Add(new TextBlock { Text = "Thinking…", Opacity = 0.65, FontSize = 12 });
        TranscriptPanel.Children.Add(panel);
        ScrollToEnd();
        return panel;
    }

    private void AppendError(string message)
    {
        TranscriptPanel.Children.Add(new TextBlock
        {
            Text = message,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.Red),
            TextWrapping = TextWrapping.Wrap,
        });
    }

    private void ScrollToEnd() => TranscriptScroller.ChangeView(null, TranscriptScroller.ScrollableHeight, null);
}
