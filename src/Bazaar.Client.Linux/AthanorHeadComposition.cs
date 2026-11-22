using Avalonia;
using Avalonia.Controls;
using Bazaar.Client.Athanor;
using Bazaar.Client.Athanor.AvaloniaEdit;
using Bazaar.Client.Athanor.Bundle;
using Bazaar.Client.Athanor.Drafts;
using Bazaar.Client.Athanor.Gtk;
using Bazaar.Client.Athanor.Hosting;
using Bazaar.Client.Athanor.Pane;
using Bazaar.Client.Athanor.Sources;
using Bazaar.Client.Athanor.Wire;
using Bazaar.Client.Chat;
using Bazaar.Client.Input;
using Bazaar.Client.Theme;
using Bazaar.Contracts.Modules;
using Bazaar.Ex;
using Bazaar.Ex.Head;

namespace Bazaar.Client.Linux;

/// <summary>
/// Composes the Athanor editor for the Linux head (doc 57 §2.2): AvaloniaEdit by default; the GTK helper (Monaco in
/// WebKitGTK, its own window) when the operator selected it and the helper and bundle are present; the plain-text host as the
/// last resort. The server file source is used when there is a connection, else offline. The choices are made by
/// <see cref="EditorHostSelector"/> and <see cref="EditorFileSourceSelector"/>. Must run on the UI thread.
/// </summary>
internal static class AthanorHeadComposition
{
    /// <param name="options">The operator settings.</param>
    /// <param name="server">Connection settings, or null for offline.</param>
    /// <param name="commands">The commands the loaded modules contributed, which build the router table behind the claimed keys.</param>
    /// <param name="log">Receives one line per skipped host and per warning.</param>
    /// <param name="bundleRoot">The Monaco bundle folder the GTK helper loads; the build's folder when null.</param>
    public static AthanorEditorServices Create(
        AthanorHeadOptions options,
        ChatConnectionSettings? server,
        IEnumerable<ClientCommandContribution> commands,
        Action<string> log,
        string? bundleRoot = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(log);

        if (options.Warning is { } warning)
        {
            log($"Athanor: {warning}");
        }

        var bundle = bundleRoot ?? AthanorBundle.DefaultRoot;
        var table = EditorRouterTable.Create(commands);
        var handoff = table is null ? null : RouterKeyHandoff.ForStrokes(RouterClaimedKeySource.InsertModeProbe(table));
        var drafts = new FileEditorDraftStore(options.DraftsDirectory, TimeProvider.System);

        var candidates = new List<EditorHostCandidate>
        {
            new(EditorHostKind.Gtk, () => GtkAvailable(options, bundle), () => CreateGtk(options, bundle, drafts, log)),
            new(EditorHostKind.AvaloniaEdit, () => true, () => new AvaloniaEditHost(handoff)),
        };
        var choice = EditorHostSelector.Select(EditorHostSelector.Order(windows: false, options.Host), candidates);
        foreach (var skipped in choice.Skipped)
        {
            log($"Athanor: editor host skipped, {skipped}");
        }

        if (choice.Kind == EditorHostKind.AvaloniaEdit && Application.Current is { } app)
        {
            AvaloniaEditStyles.Include(app);
        }

        AthanorWireConnection? connection = null;
        var source = EditorFileSourceSelector.Select(
            server is null
                ? null
                : () =>
                {
                    connection = AthanorWireConnection.Create(server.Server, server.Token, "linux", "bazaar-linux");
                    return connection.Source;
                },
            log);

        return new AthanorEditorServices(choice.Host, source, drafts, choice.Kind, choice.Skipped, connection);
    }

    /// <summary>True when the helper is configured and exists and the bundle folder holds the page the helper loads.</summary>
    internal static bool GtkAvailable(AthanorHeadOptions options, string bundleRoot) =>
        options.GtkHelperPath is { } helper
        && File.Exists(helper)
        && File.Exists(Path.Combine(bundleRoot, AthanorBundle.HostPage));

    /// <summary>
    /// Makes the Athanor commands reachable by key (doc 57 §2.2, §8): a mnemonic host on <paramref name="top"/> for the loaded
    /// modules' commands, with the Athanor commands handed to <paramref name="athanor"/>. Mnemonics are typed text plus Enter;
    /// no Ctrl chord is claimed. Other modules' commands have no handler yet and are reported as such. Null when the mnemonics
    /// do not resolve. Call on the UI thread, once the editor pane is registered (so <see cref="AthanorPaneModule.CommandHandler"/> is set).
    /// </summary>
    public static MnemonicHeadHost? StartCommands(
        TopLevel top,
        IEnumerable<ClientCommandContribution> commands,
        AthanorPaneModule athanor,
        AthanorHeadOptions options,
        Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(athanor);
        ArgumentNullException.ThrowIfNull(options);
        var dispatcher = new ClientCommandDispatcher();
        // The open and new commands ask for a path in an overlay on this window; a cancelled prompt does nothing.
        if (athanor.CommandHandler is { } handler)
        {
            handler.Prompts = new AvaloniaEditorCommandPrompts(top);
        }

        dispatcher.Add(AthanorCommandHandler.Handles, athanor.RunCommandAsync);
        return MnemonicHeadHost.Start(top, commands, Path.Combine(options.DataDirectory, "mnemonics.json"), dispatcher, log);
    }

    /// <summary>
    /// Keeps the editor on the app theme (doc 57 §2.2): the theme comes from <c>theme.json</c> in the data folder when it validates,
    /// else the built-in dark theme, and the editor host gets it now and on every change of the returned service. Call on the
    /// UI thread after <see cref="Create"/>. Dispose the sync at exit.
    /// </summary>
    public static (ClientThemeService Theme, EditorThemeSync Sync) StartTheme(AthanorHeadOptions options, AthanorEditorServices services, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(services);
        var theme = ClientThemeService.Create(Path.Combine(options.DataDirectory, "theme.json"), log);
        var sync = new EditorThemeSync(new ClientThemeEditorSource(theme), () => services.Host, log);
        _ = sync.StartAsync();
        return (theme, sync);
    }

    private static GtkEditorHost CreateGtk(AthanorHeadOptions options, string bundleRoot, IEditorDraftStore drafts, Action<string> log)
    {
        var gtkOptions = new GtkEditorHostOptions();
        var launcher = ProcessEditorHelperLauncher.ForHelper(options.GtkHelperPath!, bundleRoot, gtkOptions, "Athanor");
        var host = new GtkEditorHost(launcher, gtkOptions, drafts);
        _ = StartAsync(host, log);
        return host;
    }

    private static async Task StartAsync(GtkEditorHost host, Action<string> log)
    {
        try
        {
            await host.StartAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log($"Athanor: the GTK editor helper did not start ({ex.Message}). Set {AthanorHeadOptions.HostVariable}=avaloniaedit to use the in-process editor.");
        }
    }
}
