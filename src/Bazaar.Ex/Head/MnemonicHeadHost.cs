using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Bazaar.Client.Input;
using Bazaar.Contracts.Modules;

namespace Bazaar.Ex.Head;

/// <summary>
/// The minimal head composition for keyboard commands (doc 57 section 2.2): the mnemonic runtime for the commands the loaded
/// modules contributed, the <see cref="MnemonicHost"/> on the main window, the live region and the palette on the window's
/// overlay layer, and a <see cref="ClientCommandDispatcher"/> that carries out each executed command. It adds no Ctrl chord.
/// A head builds it on the UI thread after the window is shown, and disposes it at exit.
/// </summary>
public sealed class MnemonicHeadHost : IDisposable
{
    /// <summary>The text that opens the palette. Matches the placeholder the editor's claimed-key table already assumes.</summary>
    public const string PaletteMnemonic = "palette";

    private readonly TopLevel _top;
    private readonly MnemonicHost _host;
    private readonly MnemonicPaletteController _palette;
    private readonly MnemonicLiveRegion _region;
    private readonly ClientCommandDispatcher _dispatcher;
    private readonly Action<string> _log;

    private MnemonicHeadHost(
        TopLevel top,
        MnemonicRuntime runtime,
        ClientCommandDispatcher dispatcher,
        MnemonicLiveRegion region,
        Action<string> log)
    {
        _top = top;
        Runtime = runtime;
        _dispatcher = dispatcher;
        _region = region;
        _log = log;
        _host = new MnemonicHost(top, runtime.Session!, region);
        _palette = new MnemonicPaletteController(() => runtime.Rows, Show, Hide);
        _host.CommandInvoked += OnCommand;
        _host.ActionRaised += (_, action) => _palette.Handle(action);
        _palette.CommandChosen += OnCommand;
    }

    public MnemonicRuntime Runtime { get; }

    public MnemonicHost Host => _host;

    public MnemonicPaletteController Palette => _palette;

    /// <summary>
    /// Builds the host. Returns null, after logging why, when the commands do not resolve to a usable table, so the head runs
    /// without keyboard commands instead of failing. Blocks briefly to read the saved remaps (one small file).
    /// </summary>
    /// <param name="top">The main window.</param>
    /// <param name="commands">The commands the loaded modules contributed.</param>
    /// <param name="overridesPath">Where this person's remaps are saved.</param>
    /// <param name="dispatcher">Where executed commands go.</param>
    /// <param name="log">Receives one line per problem.</param>
    public static MnemonicHeadHost? Start(
        TopLevel top,
        IEnumerable<ClientCommandContribution> commands,
        string overridesPath,
        ClientCommandDispatcher dispatcher,
        Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(top);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(log);

        var defaults = MnemonicAssignment.FromContributions(commands);
        var runtime = Task.Run(() => MnemonicRuntime.CreateAsync(
            defaults,
            new FileMnemonicOverrideStore(overridesPath),
            PaletteMnemonic.Select(c => KeyStroke.OfChar(c)),
            new MnemonicNoticeLog(log))).GetAwaiter().GetResult();
        if (runtime.Warning is { } warning)
        {
            log($"Mnemonics: {warning}");
        }

        if (runtime.Session is null)
        {
            return null;
        }

        var region = new MnemonicLiveRegion();
        var host = new MnemonicHeadHost(top, runtime, dispatcher, region, log);
        host.MountRegion();
        return host;
    }

    public void Dispose()
    {
        _top.Opened -= OnOpened;
        _host.Dispose();
        Hide(null);
        if (OverlayLayer.GetOverlayLayer(_top) is { } layer)
        {
            layer.Children.Remove(_region.Control);
        }
    }

    // The overlay layer exists only once the window's template is applied, so mount now if it is there, else when the window opens.
    private void MountRegion()
    {
        if (OverlayLayer.GetOverlayLayer(_top) is { } layer)
        {
            if (!layer.Children.Contains(_region.Control))
            {
                layer.Children.Add(_region.Control);
            }

            return;
        }

        _top.Opened += OnOpened;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        _top.Opened -= OnOpened;
        MountRegion();
    }

    private async void OnCommand(object? sender, string commandId)
    {
        try
        {
            var result = await _dispatcher.DispatchAsync(commandId).ConfigureAwait(true);
            if (result.Notice is { } notice)
            {
                _log($"Command {commandId}: {notice}");
                _region.Announce(notice, AnnouncePriority.Assertive);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log($"Command {commandId} failed: {ex.Message}");
        }
    }

    private void Show(MnemonicPaletteView view)
    {
        if (OverlayLayer.GetOverlayLayer(_top) is not { } layer)
        {
            return;
        }

        view.HorizontalAlignment = HorizontalAlignment.Center;
        view.VerticalAlignment = VerticalAlignment.Top;
        view.Margin = new Thickness(0, 48, 0, 0);
        view.MinWidth = 360;
        layer.Children.Add(view);
        view.FocusQuery();
    }

    private void Hide(MnemonicPaletteView? view)
    {
        view ??= _palette.View;
        if (view is not null && OverlayLayer.GetOverlayLayer(_top) is { } layer)
        {
            layer.Children.Remove(view);
        }
    }
}
