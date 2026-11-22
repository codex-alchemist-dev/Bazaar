using Avalonia.Controls;
using Bazaar.Client.Input;

namespace Bazaar.Ex.Head;

/// <summary>
/// The one object a head creates to get keyboard mnemonics: it joins the <see cref="MnemonicRuntime"/> to the window through a
/// <see cref="MnemonicHost"/> and drives the palette from the router's palette actions. When the runtime changes (a remap or reset),
/// the shell rebuilds the host on the new session, so a remap takes effect on the next key. A command chosen by mnemonic or from the
/// palette is reported through <see cref="CommandInvoked"/>; the shell never decides what a command does. The one exception is
/// the remap command, which the shell reports as <see cref="RemapRequested"/> so the head can open the remap screen from a
/// mnemonic or from the palette in the same way.
/// </summary>
public sealed class MnemonicShell : IDisposable
{
    private readonly TopLevel _top;
    private readonly MnemonicRuntime _runtime;
    private readonly IMnemonicAnnouncer _announcer;
    private readonly MnemonicPaletteController _palette;
    private MnemonicHost? _host;

    public MnemonicShell(
        TopLevel top,
        MnemonicRuntime runtime,
        IMnemonicAnnouncer announcer,
        Action<MnemonicPaletteView> showPalette,
        Action<MnemonicPaletteView> hidePalette)
    {
        _top = top ?? throw new ArgumentNullException(nameof(top));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _announcer = announcer ?? throw new ArgumentNullException(nameof(announcer));
        ArgumentNullException.ThrowIfNull(showPalette);
        ArgumentNullException.ThrowIfNull(hidePalette);

        _palette = new MnemonicPaletteController(() => _runtime.Rows, showPalette, hidePalette);
        _palette.CommandChosen += (_, id) => Route(id);
        _runtime.Changed += OnRuntimeChanged;
        Attach();
    }

    /// <summary>A command was chosen by mnemonic and Enter, or from the palette.</summary>
    public event EventHandler<string>? CommandInvoked;

    /// <summary>The remap command was chosen by mnemonic or from the palette. The head opens <see cref="MnemonicRemapScreen"/>.</summary>
    public event EventHandler? RemapRequested;

    /// <summary>Shift+Enter asked for AI suggestions for the typed text. Proposals only; the user confirms each one.</summary>
    public event EventHandler<string>? AiSuggestRequested;

    /// <summary>The expansion to show beside the typed text before Enter runs it. Null when nothing matches yet.</summary>
    public event EventHandler<string?>? OverlayChanged;

    /// <summary>True while the palette is open.</summary>
    public bool IsPaletteOpen => _palette.IsOpen;

    /// <summary>True when mnemonics are active. False only when the default mnemonics did not resolve.</summary>
    public bool IsActive => _host is not null;

    public void Dispose()
    {
        _runtime.Changed -= OnRuntimeChanged;
        Detach();
    }

    private void OnRuntimeChanged(object? sender, EventArgs e)
    {
        Detach();
        Attach();
    }

    private void Attach()
    {
        if (_runtime.Session is not { } session)
        {
            return;
        }

        var host = new MnemonicHost(_top, session, _announcer);
        host.CommandInvoked += (_, id) => Route(id);
        host.AiSuggestRequested += (_, typed) => AiSuggestRequested?.Invoke(this, typed);
        host.OverlayChanged += (_, overlay) => OverlayChanged?.Invoke(this, overlay);
        host.ActionRaised += (_, action) => _palette.Handle(action);
        _host = host;
    }

    private void Route(string commandId)
    {
        if (commandId == NavigationDefaults.RemapCommandId)
        {
            RemapRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        CommandInvoked?.Invoke(this, commandId);
    }

    private void Detach()
    {
        _host?.Dispose();
        _host = null;
    }
}
