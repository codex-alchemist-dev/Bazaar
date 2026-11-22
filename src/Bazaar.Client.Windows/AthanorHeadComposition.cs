using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Bazaar.Client.Athanor;
using Bazaar.Client.Athanor.AvaloniaEdit;
using Bazaar.Client.Athanor.Bundle;
using Bazaar.Client.Athanor.Drafts;
using Bazaar.Client.Athanor.Hosting;
using Bazaar.Client.Athanor.Pane;
using Bazaar.Client.Athanor.Sources;
using Bazaar.Client.Athanor.WebView2;
using Bazaar.Client.Athanor.Wire;
using Bazaar.Client.Chat;
using Bazaar.Client.Input;
using Bazaar.Client.Theme;
using Bazaar.Contracts.Modules;
using Bazaar.Ex;
using Bazaar.Ex.Head;

namespace Bazaar.Client.Windows;

/// <summary>
/// Composes the Athanor editor for the Windows head (doc 57 §2.2): WebView2 when its runtime and the bundle are there, then
/// AvaloniaEdit, then the plain-text host; the server file source when there is a connection, else offline. The choices
/// are made by <see cref="EditorHostSelector"/> and <see cref="EditorFileSourceSelector"/>; this class only supplies the
/// Windows checks and factories. Must run on the UI thread, after Avalonia started.
/// </summary>
internal static class AthanorHeadComposition
{
    /// <summary>Default data folder: the per-user local application data folder, under Bazaar.</summary>
    public static string DefaultDataDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bazaar");

    /// <param name="options">The operator settings.</param>
    /// <param name="server">Connection settings, or null for offline.</param>
    /// <param name="commands">The commands the loaded modules contributed, which build the router table behind the claimed keys.</param>
    /// <param name="log">Receives one line per skipped host and per warning.</param>
    /// <param name="webView2Available">Overrides the WebView2 check; tests pass false. Null runs the real check.</param>
    public static AthanorEditorServices Create(
        AthanorHeadOptions options,
        ChatConnectionSettings? server,
        IEnumerable<ClientCommandContribution> commands,
        Action<string> log,
        Func<bool>? webView2Available = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(log);

        if (options.Warning is { } warning)
        {
            log($"Athanor: {warning}");
        }

        var table = EditorRouterTable.Create(commands);
        var claims = table is null ? null : new RouterClaimedKeySource(RouterClaimedKeySource.InsertModeProbe(table));
        var handoff = table is null ? null : RouterKeyHandoff.ForStrokes(RouterClaimedKeySource.InsertModeProbe(table));

        var candidates = new List<EditorHostCandidate>
        {
            new(EditorHostKind.WebView2, () => (webView2Available ?? WebView2Available)(), () => OperatingSystem.IsWindows() ? CreateWebView2Windows(options, handoff, claims) : throw new PlatformNotSupportedException("WebView2 needs Windows.")),
            new(EditorHostKind.AvaloniaEdit, () => true, () => new AvaloniaEditHost(handoff)),
        };
        var choice = EditorHostSelector.Select(EditorHostSelector.Order(windows: true, options.Host), candidates);
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
                    connection = AthanorWireConnection.Create(server.Server, server.Token, "windows", "bazaar-windows");
                    return connection.Source;
                },
            log);

        var drafts = new FileEditorDraftStore(options.DraftsDirectory, TimeProvider.System);
        return new AthanorEditorServices(choice.Host, source, drafts, choice.Kind, choice.Skipped, connection);
    }

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

    private static bool WebView2Available() =>
        OperatingSystem.IsWindows() && WebView2Probe();

    [SupportedOSPlatform("windows")]
    private static bool WebView2Probe() =>
        AthanorWebView2Environment.IsRuntimeAvailable() && AthanorBundle.Verify().Complete;

    [SupportedOSPlatform("windows")]
    private static WebView2EditorHost CreateWebView2Windows(AthanorHeadOptions options, IEditorKeyHandoff? handoff, IClaimedKeySource? claims) =>
        new(new WebView2HostOptions { UserDataFolder = options.WebView2UserDataFolder }, handoff, claims);
}
