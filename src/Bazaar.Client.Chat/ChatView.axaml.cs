using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Bazaar.Client.Chat.Abstractions;
using Bazaar.Client.Motion;
using Bazaar.Client.Motion.Avalonia;

namespace Bazaar.Client.Chat;

/// <summary>The group chat view: lines, a draft box and a send button. Its DataContext is an <see cref="IChatViewModel"/>.</summary>
public sealed partial class ChatView : UserControl
{
    public ChatView()
    {
        InitializeComponent();

        SendButton.Click += OnSendClick;
        DraftBox.KeyDown += OnDraftKeyDown;
    }

    private MessageListMotion? _lineMotion;
    private ButtonMotion? _sendMotion;

    /// <summary>The send button's motion while the view is loaded, for tests.</summary>
    internal ButtonMotion? SendMotion => _sendMotion;

    // Motion lives while the view is in a window, so a long-lived motion state never keeps a closed view alive.
    // The send launch plays before the send clears the draft, so it still sees the text.
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        _lineMotion ??= MessageListMotion.Attach(Lines, item => item is ChatLine { Own: true } ? DropletKind.OwnSent : DropletKind.Arrival);
        _sendMotion ??= ButtonMotion.AttachSend(SendButton, DraftBox);
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        base.OnUnloaded(e);
        _lineMotion?.Dispose();
        _lineMotion = null;
        _sendMotion?.Dispose();
        _sendMotion = null;
    }

    private async void OnSendClick(object? sender, RoutedEventArgs e)
    {
        _sendMotion?.LaunchIfArmed();
        await SendDraftAsync();
    }

    private async void OnDraftKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
        e.Handled = true;
        _sendMotion?.LaunchIfArmed();
        await SendDraftAsync();
    }

    private async Task SendDraftAsync()
    {
        if (DataContext is IChatViewModel vm) await vm.SendDraftAsync();
    }
}
