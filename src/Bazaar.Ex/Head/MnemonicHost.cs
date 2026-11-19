using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Bazaar.Client.Core.Input;

namespace Bazaar.Ex.Head;

/// <summary>
/// Connects a <see cref="MnemonicSession"/> to a top-level window. It sees keys on the tunnelling route, so it runs
/// before the focused control. It only marks a key handled when the router consumed it, so text typed in Insert mode
/// still reaches the text box. Palette actions are raised for the palette view, which is not part of this change.
/// </summary>
public sealed class MnemonicHost : IDisposable
{
    private readonly TopLevel _top;
    private readonly MnemonicSession _session;
    private readonly IMnemonicAnnouncer _announcer;

    public MnemonicHost(TopLevel top, MnemonicSession session, IMnemonicAnnouncer announcer)
    {
        _top = top ?? throw new ArgumentNullException(nameof(top));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _announcer = announcer ?? throw new ArgumentNullException(nameof(announcer));
        _top.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    /// <summary>A navigation or command mnemonic ran on Enter. The shell carries out the command.</summary>
    public event EventHandler<string>? CommandInvoked;

    /// <summary>A palette or focus action the router asked for. Subscribed by the palette view.</summary>
    public event EventHandler<RouterAction>? ActionRaised;

    /// <summary>The expansion to show beside the typed text before Enter runs it. Null when nothing matches yet.</summary>
    public event EventHandler<string?>? OverlayChanged;

    public void Dispose() => _top.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!MnemonicKeyMapper.TryMap(e.Key, e.KeySymbol, e.KeyModifiers, out var stroke))
        {
            return;
        }

        var step = _session.Handle(stroke, CurrentContext());
        if (step.Outcome == RouteOutcome.Passthrough)
        {
            return;
        }

        e.Handled = true;
        OverlayChanged?.Invoke(this, step.Overlay);
        if (step.Speech is { } speech)
        {
            _announcer.Announce(speech, step.Priority);
        }

        if (step.Action is { } action)
        {
            RunAction(action);
        }
        else if (step.Outcome == RouteOutcome.Executed && step.CommandId is { } commandId)
        {
            CommandInvoked?.Invoke(this, commandId);
        }
    }

    private void RunAction(RouterAction action)
    {
        switch (action.Kind)
        {
            case RouterActionKind.FocusTextInput:
                FirstTextInput()?.Focus();
                break;
            case RouterActionKind.ReleaseTextFocus:
                _top.FocusManager?.Focus(null);
                break;
            default:
                ActionRaised?.Invoke(this, action);
                break;
        }
    }

    private RouterContext CurrentContext()
    {
        var focused = _top.FocusManager?.GetFocusedElement();
        return new RouterContext(
            TextInputFocused: focused is TextBox,
            HasFocusableTextInput: FirstTextInput() is not null,
            ImeComposing: false);
    }

    private TextBox? FirstTextInput() =>
        _top.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.IsEffectivelyVisible && t.IsEffectivelyEnabled);
}
