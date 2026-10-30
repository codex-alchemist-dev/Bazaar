using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Bazaar.Client.Core.Chat;

/// <summary>The group chat view: lines, a draft box and a send button. Its DataContext is a <see cref="ChatViewModel"/>.</summary>
public sealed partial class ChatView : UserControl
{
    public ChatView()
    {
        InitializeComponent();
        SendButton.Click += OnSendClick;
        DraftBox.KeyDown += OnDraftKeyDown;
    }

    private async void OnSendClick(object? sender, RoutedEventArgs e) => await SendDraftAsync();

    private async void OnDraftKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
        e.Handled = true;
        await SendDraftAsync();
    }

    private async Task SendDraftAsync()
    {
        if (DataContext is ChatViewModel vm) await vm.SendDraftAsync();
    }
}
