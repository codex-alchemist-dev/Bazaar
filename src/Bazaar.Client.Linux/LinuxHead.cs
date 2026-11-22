using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Bazaar.Accessibility;
using Bazaar.Client.Abstractions.Server;
using Bazaar.Client.Abstractions.Wire;
using Bazaar.Client.Calcinato.Session;
using Bazaar.Client.Chat;
using Bazaar.Client.Chat.Abstractions;
using Bazaar.Client.Constellation;
using Bazaar.Client.Constellation.Abstractions;
using Bazaar.Client.Core;
using Bazaar.Client.Core.Panes;
using Bazaar.Client.DefaultApp.Linux;
using Bazaar.Client.Gc;
using Bazaar.Client.Linux.Updates;
using Bazaar.Client.Modules;
using Bazaar.Client.Navigation;
using Bazaar.Client.Navigation.Abstractions;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Client.Preferences;
using Bazaar.Client.Preferences.Abstractions;
using Bazaar.Client.RyuseiFuda;
using Bazaar.Client.Shell;
using Bazaar.Client.Views;
using Bazaar.Contracts.Modules;
using Bazaar.Contracts.Panes;
using Bazaar.Ex.Head;
using Bazaar.Localisation;
using Bazaar.SupportBundle;

namespace Bazaar.Client.Linux;

/// <summary>What <see cref="LinuxHead"/> needs from the process around it.</summary>
/// <param name="Settings">The dev chat settings from the environment (doc 30 section 1). Null means no chat is configured.</param>
/// <param name="Paths">Where this head keeps its per-user state.</param>
/// <param name="Args">The command line.</param>
/// <param name="Updates">The update host whose <c>update.check</c> command the palette runs.</param>
/// <param name="DataDir">The client data directory the support bundle reads and writes.</param>
/// <param name="ClientBuild">Build label sent in the chat <c>hello</c>.</param>
/// <param name="Log">Receives one line per problem.</param>
internal sealed record LinuxHeadOptions(
    ChatConnectionSettings? Settings,
    LinuxPaths Paths,
    IReadOnlyList<string> Args,
    UpdateHost Updates,
    string DataDir,
    string ClientBuild,
    Action<string> Log);

/// <summary>
/// The Linux head's pane host (doc 30 section 1, doc 28): composes the pane modules into a registry, shows one pane at a time in
/// the main window, and gives the palette its commands. Navigation, the command runner and the preference store share the
/// <see cref="ViewActionRouter"/> the modules' view documents act through (doc 41c section 6). The dev chat settings from the
/// environment still start a chat, which the GC pane then shows.
/// </summary>
internal sealed class LinuxHead : IAsyncDisposable
{
    private static readonly JsonElement _emptyModel = JsonElement.Parse("{}");

    private readonly ContentControl _slot = new();
    private readonly CommandRunner _commands = new();
    private readonly LatePaneSurface _surface = new();
    private readonly TerminalSessionManager _terminals = CalcinatoTerminalPanes.CreateSessions();
    private readonly ChatHost? _chat;
    private readonly LinuxHeadOptions _options;
    private int _disposed;

    private LinuxHead(LinuxHeadOptions options)
    {
        _options = options;
        var log = options.Log;

        // Panes and the Constellation tiles come from the company server this build was made for (doc 37 section 4a).
        try
        {
            ClientServer.ConnectToCompanyServer();
        }
        catch (InvalidOperationException ex)
        {
            log($"Bazaar: {ex.Message}");
        }

        Directory.CreateDirectory(options.Paths.StateDirectory);
        var modules = LinuxModules.Create();
        Preferences = new PreferenceStore(
            modules.OfType<IPreferenceDeclarer>().SelectMany(m => m.Preferences),
            new FilePreferenceStorage(options.Paths.PreferencesFile, log));
        var viewActions = ViewActionRouter.ForServices(new PaneNavigator(_surface), _commands, Preferences);

        if (options.Settings is { } settings)
        {
            _chat = ChatHost.Start(settings, new FileCursorStore(options.Paths.CursorFile), "linux", options.ClientBuild, action => Dispatcher.UIThread.Post(action), log, options.Updates.OnWelcome);
        }

        var context = new PaneModuleContext(Report: message => log($"Bazaar: {message}"))
            .With<IConstellationTileSource>(HttpConstellationTileSource.Create(ClientServer.Current))
            .WithViewContext(ViewContextServices.CreateDefault(viewActions, ex => log($"Bazaar: view action failed: {ex.Message}")));
        if (_chat is not null)
        {
            context = context.With<IChatViewModel>(_chat.Model).With<IChatViewFactory>(ChatViewFactory.Instance);
        }

        // Every module's manifest is checked the same way (doc 41 section 8); a module that fails adds nothing.
        Loaded = ClientModuleHost.Load(
            modules.OfType<IClientModule>().Append(new LinuxHeadModule()).Append(new SupportBundleClientModule()),
            ClientHostFacts.Current(),
            log);
        var loadedIds = Loaded.Loaded.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        var shipped = modules.Where(m => m is not IClientModule client || loadedIds.Contains(client.Manifest.Id));
        Registry = CalcinatoTerminalPanes.Compose(PaneViews.Instance, shipped, context, _terminals);

        RegisterCommands();
        _surface.Bind(Registry.Contains, ShowAsync);

        Window = new MainWindow { Content = _slot };
        Show(InitialKind(options));
        if (FirstRunScreens.TryBuild(options.Args) is { } firstRun)
        {
            Window.Content = firstRun;
        }

        // Direction from the locale and the screen-reader live region wrap whatever the window shows (doc 41c).
        ShellHost.Install(Window, new SystemLocaleSource(), new AnnouncementGate(TimeProvider.System));
        Window.Opened += async (_, _) => await AttachPaletteAsync();
        Window.Closed += async (_, _) => await DisposeAsync();
    }

    /// <summary>The main window. Its content is the pane slot, wrapped by the shell chrome.</summary>
    public MainWindow Window { get; }

    /// <summary>The composed pane registry.</summary>
    public PaneRegistry Registry { get; }

    /// <summary>The device's preferences, kept in <see cref="LinuxPaths.PreferencesFile"/>.</summary>
    public PreferenceStore Preferences { get; }

    /// <summary>What the module host loaded. <see cref="ClientModuleLoadResult.Commands"/> is what the palette lists.</summary>
    public ClientModuleLoadResult Loaded { get; }

    /// <summary>The pane kind now in the window, or null before the first pane.</summary>
    public string? ShownKind { get; private set; }

    /// <summary>Runs the registered command with <paramref name="commandId"/>; a failure is logged, never thrown.</summary>
    public async Task RunCommandAsync(string commandId)
    {
        try
        {
            await _commands.RunAsync(commandId).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is NavigationException or InvalidOperationException or IOException or NotSupportedException)
        {
            _options.Log($"Bazaar: command '{commandId}' failed: {ex.Message}");
        }
    }

    /// <summary>Shows <paramref name="paneKind"/> in the window. Marshals to the UI thread when called from another.</summary>
    public Task ShowAsync(string paneKind, CancellationToken cancellationToken = default)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            Show(paneKind);
            return Task.CompletedTask;
        }

        return Dispatcher.UIThread.InvokeAsync(() => Show(paneKind), DispatcherPriority.Normal, cancellationToken).GetTask();
    }

    /// <summary>Builds the head for the process. Call on the UI thread.</summary>
    public static LinuxHead Create(LinuxHeadOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new LinuxHead(options);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_chat is not null)
        {
            await _chat.DisposeAsync().ConfigureAwait(false);
        }

        await _terminals.DisposeAsync().ConfigureAwait(false);
    }

    // With dev chat settings the window opens on the GC pane; otherwise on the Dashboard (doc 28). --terminal opens the terminal.
    private static string InitialKind(LinuxHeadOptions options) =>
        options.Args.Contains("--terminal", StringComparer.Ordinal) ? PaneKinds.Terminal
        : options.Settings is not null ? GcPaneViewModel.PaneKind
        : PaneKinds.Dashboard;

    private void Show(string paneKind)
    {
        if (!Registry.Contains(paneKind))
        {
            throw new NavigationException($"No module provides the pane '{paneKind}'.");
        }

        if (paneKind == PaneKinds.Terminal && _terminals.Sessions.Count == 0)
        {
            _terminals.OpenLocal();
        }

        var descriptor = paneKind == PaneKinds.Kanban
            ? RyuseiFudaPanes.WindowPane
            : new PaneDescriptor(paneKind, paneKind, string.Empty, _emptyModel);
        _slot.Content = new ScrollViewer { Content = PaneHost.Build([descriptor], Registry) };
        ShownKind = paneKind;
    }

    private void RegisterCommands()
    {
        foreach (var (commandId, paneKind) in LinuxHeadModule.ModuleNavigation)
        {
            _commands.Register(commandId, ct => ShowAsync(paneKind, ct));
        }

        foreach (var paneCommand in LinuxHeadModule.PaneCommands)
        {
            _commands.Register(paneCommand.Command.Id, ct => ShowAsync(paneCommand.PaneKind, ct));
        }

        _commands.Register(UpdateCoordinator.CheckCommandId, ct => _options.Updates.CreateCheckCommand(() => Window).RunAsync(ct));
        _commands.Register(LinuxHeadModule.QuitCommandId, _ =>
        {
            Quit();
            return Task.CompletedTask;
        });
        _commands.Register(SupportBundleClientModule.SaveCommandId, ct =>
            Task.Run(() => SupportBundleCommands.SaveFromPalette(_options.DataDir, TimeProvider.System, _options.Log), ct));
        foreach (var command in DefaultAppCommands.All)
        {
            _commands.Register(command.Id, ct => ReportAsync(command.Title, id => DefaultAppCommands.RunAsync(id, new XdgDefaultAppRegistrar(new DefaultAppSettings { ExecPath = ExecPath() }, new SystemProcessRunner()), ct), command.Id));
        }

        foreach (var command in DefaultTerminalCommands.All)
        {
            _commands.Register(command.Id, ct => ReportAsync(command.Title, id => DefaultTerminalCommands.RunAsync(id, new XdgDefaultTerminalRegistrar(new DefaultTerminalSettings { ExecPath = ExecPath() }, new SystemProcessRunner()), ct), command.Id));
        }
    }

    private static string ExecPath() => Environment.ProcessPath ?? string.Empty;

    // Registration is refused for a relative path or the dotnet host, as the command-line flags do; the notice says why.
    private async Task ReportAsync(string title, Func<string, Task<string?>> run, string commandId)
    {
        string? text;
        try
        {
            text = DefaultAppCommandLine.RefuseProcessPath(Environment.ProcessPath) ?? await run(commandId).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            text = $"Refused: {ex.Message}";
        }

        _options.Log($"Bazaar: {title}: {text}");
        await Dispatcher.UIThread.InvokeAsync(() => StatusNotice.Show(Window, title, text ?? "Done."));
    }

    private void Quit()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
        else
        {
            Window.Close();
        }
    }

    // Keyboard mnemonics and the palette (doc 41). The loaded modules' commands are what the palette lists.
    private async Task AttachPaletteAsync()
    {
        var head = await MnemonicHeadHost.AttachAsync(Window, _options.Paths.MnemonicOverridesFile, Loaded.Commands);
        head.Shell.CommandInvoked += (_, id) => _ = RunCommandAsync(id);
    }
}
