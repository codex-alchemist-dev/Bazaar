using Avalonia.Controls;
using Bazaar.Client.Input;

namespace Bazaar.Ex.Head;

/// <summary>
/// Joins the <see cref="MnemonicRuntime"/>, the <see cref="MnemonicShell"/> and the <see cref="MnemonicRemapScreen"/> for one head.
/// The head supplies the window, the override store, the announcer, and where overlays appear (show and hide). The host opens the
/// remap screen when the shell asks for it, and saves each accepted change through the runtime, which rebuilds the session so the
/// change applies to the next key.
/// </summary>
public sealed class MnemonicHeadHost : IDisposable
{
    private static readonly KeyStroke[] _paletteKeys = [KeyStroke.OfChar('p')];

    private readonly MnemonicRuntime _runtime;
    private readonly MnemonicShell _shell;
    private readonly Action<Control> _show;
    private readonly Action<Control> _hide;
    private readonly bool _actorIsOwner;
    private MnemonicRemapScreen? _remap;

    private MnemonicHeadHost(MnemonicRuntime runtime, MnemonicShell shell, Action<Control> show, Action<Control> hide, bool actorIsOwner)
    {
        _runtime = runtime;
        _shell = shell;
        _show = show;
        _hide = hide;
        _actorIsOwner = actorIsOwner;
        _shell.RemapRequested += (_, _) => OpenRemap();
    }

    /// <summary>The runtime holding the person's current mnemonic document.</summary>
    public MnemonicRuntime Runtime => _runtime;

    /// <summary>The shell that reads keys and runs commands.</summary>
    public MnemonicShell Shell => _shell;

    /// <summary>The last remap that the runtime refused or could not save, or null when none has failed.</summary>
    public string? LastError { get; private set; }

    /// <summary>Starts mnemonics on <paramref name="top"/>. Overlays appear through <paramref name="show"/> and leave through <paramref name="hide"/>.</summary>
    public static async Task<MnemonicHeadHost> StartAsync(
        TopLevel top,
        IMnemonicOverrideStore store,
        IMnemonicAnnouncer announcer,
        Action<Control> show,
        Action<Control> hide,
        bool actorIsOwner = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(top);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(announcer);
        ArgumentNullException.ThrowIfNull(show);
        ArgumentNullException.ThrowIfNull(hide);

        var runtime = await MnemonicRuntime.CreateAsync(
            NavigationDefaults.Assignments,
            store,
            _paletteKeys,
            new MnemonicNoticeLog(_ => { }),
            actorIsOwner,
            ct: ct).ConfigureAwait(true);
        var shell = new MnemonicShell(top, runtime, announcer, view => show(view), view => hide(view));
        return new MnemonicHeadHost(runtime, shell, show, hide, actorIsOwner);
    }

    /// <summary>
    /// Starts mnemonics on a head window. The person's overrides live in <paramref name="overridesPath"/>, and overlays open as
    /// child windows of <paramref name="window"/>. Heads call this once the window is opened.
    /// </summary>
    public static Task<MnemonicHeadHost> AttachAsync(Window window, string overridesPath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentException.ThrowIfNullOrWhiteSpace(overridesPath);

        var overlay = new MnemonicOverlay(window);
        return StartAsync(window, new FileMnemonicOverrideStore(overridesPath), new NullMnemonicAnnouncer(), overlay.Show, overlay.Hide, ct: ct);
    }

    /// <summary>Opens the remap screen. Does nothing when it is already open.</summary>
    public void OpenRemap()
    {
        if (_remap is not null)
        {
            return;
        }

        var screen = new MnemonicRemapScreen(NavigationDefaults.Assignments, _runtime.Document, _paletteKeys, _actorIsOwner);
        screen.DocumentChanged += (_, next) => _ = ApplyAsync(_runtime.Document, next, CancellationToken.None);
        screen.Closed += (_, _) => CloseRemap();
        _remap = screen;
        _show(screen);
        screen.FocusList();
    }

    /// <summary>Closes the remap screen if it is open.</summary>
    public void CloseRemap()
    {
        if (_remap is null)
        {
            return;
        }

        var screen = _remap;
        _remap = null;
        _hide(screen);
    }

    /// <summary>Brings the runtime to <paramref name="next"/> by remapping or resetting each command that differs.</summary>
    internal async Task ApplyAsync(MnemonicOverrideDocument previous, MnemonicOverrideDocument next, CancellationToken ct)
    {
        var before = previous.Overrides.ToDictionary(o => o.CommandId, o => o.Mnemonic, StringComparer.Ordinal);
        var after = next.Overrides.ToDictionary(o => o.CommandId, o => o.Mnemonic, StringComparer.Ordinal);

        foreach (var commandId in before.Keys.Where(id => !after.ContainsKey(id)).ToList())
        {
            var reset = await _runtime.ResetAsync(commandId, ct).ConfigureAwait(true);
            LastError = reset.Accepted ? null : reset.Reason;
        }

        foreach (var (commandId, mnemonic) in after)
        {
            if (before.TryGetValue(commandId, out var old) && old == mnemonic)
            {
                continue;
            }

            var remap = await _runtime.RemapAsync(commandId, mnemonic, ct).ConfigureAwait(true);
            LastError = remap.Accepted ? null : remap.Reason;
        }
    }

    public void Dispose()
    {
        CloseRemap();
        _shell.Dispose();
    }
}
