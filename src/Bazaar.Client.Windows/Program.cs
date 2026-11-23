using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Bazaar.Accessibility;
using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Abstractions.Server;
using Bazaar.Client.Abstractions.Wire;
using Bazaar.Client.Athanor;
using Bazaar.Client.Athanor.Drafts;
using Bazaar.Client.Athanor.Hosting;
using Bazaar.Client.Athanor.Pane;
using Bazaar.Client.Athanor.Sources;
using Bazaar.Client.Calcinato.Panes;
using Bazaar.Client.Calendar;
using Bazaar.Client.Chat;
using Bazaar.Client.ColdArchive;
using Bazaar.Client.Components.Chat;
using Bazaar.Client.Components.Launcher;
using Bazaar.Client.Constellation;
using Bazaar.Client.Constellation.Abstractions;
using Bazaar.Client.Core;
using Bazaar.Client.Core.Panes;
using Bazaar.Client.Dashboard;
using Bazaar.Client.Engines;
using Bazaar.Client.Gc;
using Bazaar.Client.GitGui;
using Bazaar.Client.GitSolarSystem.Abstractions;
using Bazaar.Client.GitSolarSystem.Http;
using Bazaar.Client.GitSolarSystem.Panes;
using Bazaar.Client.Interrupts;
using Bazaar.Client.Launcher;
using Bazaar.Client.Modules;
using Bazaar.Client.Motion;
using Bazaar.Client.Motion.Avalonia;
using Bazaar.Client.Navigation;
using Bazaar.Client.Org;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Client.Posture;
using Bazaar.Client.Preferences;
using Bazaar.Client.Preferences.Abstractions;
using Bazaar.Client.Preload;
using Bazaar.Client.RyuseiFuda;
using Bazaar.Client.Settings;
using Bazaar.Client.Shell;
using Bazaar.Client.SignIn;
using Bazaar.Client.Theme;
using Bazaar.Client.Vault;
using Bazaar.Client.Views;
using Bazaar.Contracts.Modules;
using Bazaar.Contracts.Panes;
using Bazaar.Ex.Head;
using Bazaar.Localisation;
using Bazaar.SupportBundle;

namespace Bazaar.Client.Windows;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Client GC: sustained low latency keeps UI frames free of blocking gen2 collections (doc 40).
        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
        // Logging first, so every later line (and a failed start) lands in the client log the support bundle reads (doc 37 section 4d).
        SupportBundleCommands.InstallLog();
        // --self-check and --support-bundle run headless and exit, before the single-instance mutex and any window.
        if (SupportBundleCommands.TryRun(args, WindowsModules.Create) is { } exitCode)
        {
            Environment.ExitCode = exitCode;
            return;
        }

        var request = LaunchArgs.Parse(args);
        var pipeName = LaunchPipe.DefaultName(Environment.UserName);
        // How long a second launch waits for the running instance to answer (doc 28, startup budgets).
        var forwardTimeout = TimeSpan.FromSeconds(2);

        // A named mutex makes the primary instance unique per user, so only one process ever listens on the pipe.
        using var singleInstance = new Mutex(initiallyOwned: true, name: $"Local\\{pipeName}", out var isPrimary);

        if (!isPrimary)
        {
            var forwarded = LaunchPipe.TryForwardAsync(pipeName, request, forwardTimeout, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            if (forwarded)
            {
                return;
            }

            // The running instance did not answer within the budget. Start without a listener and warn, as doc 28 asks.
            Console.Error.WriteLine("Bazaar: the running instance did not answer; this launch will not be forwarded.");
        }

        using var launcher = isPrimary ? LauncherHost.Start(pipeName, Console.Error.WriteLine) : null;

        var chatSettings = ChatSettings(args);
        ChatHost? chat = null;
        // The Studio bridge (doc 20): off unless engine.studio.mcp_path (BAZAAR_STUDIO_MCP_PATH) names a Studio MCP executable.
        IAsyncDisposable? studio = null;

        // Panes come from the first-party registry; the Constellation tiles come from the company server this build was made for (doc 37 section 4a).
        ClientServer.ConnectToCompanyServer();
        // The Dashboard comes first, then Constellation, then the GC pane, then Vault, then the rest of RyuseiFuda, then Git Solar System (pager order).
        var athanorModule = new AthanorPaneModule();
        var modules = WindowsModules.Create(athanorModule);
        // The four view actions (doc 41c section 6) go to the navigator, the command runner and the preference store. The surface is bound
        // once the registry and the window exist; until then a pane cannot be shown. Preferences stay on this device until a wire sync is attached.
        var paneSurface = new LatePaneSurface();
        var preferences = new PreferenceStore(
            modules.OfType<IPreferenceDeclarer>().SelectMany(m => m.Preferences),
            new FilePreferenceStorage(PreferencesPath(), Console.Error.WriteLine));
        var viewActions = ViewActionRouter.ForServices(new PaneNavigator(paneSurface), new CommandRunner(), preferences);
        var terminals = CalcinatoTerminalPanes.CreateSessions();
        // The Git Solar System feed has no Client.Core type, so the head hands it over through PaneModuleContext.With.
        var features = CalcinatoTerminalPanes.AddServices(
            new PaneModuleContext()
                .With<IConstellationTileSource>(HttpConstellationTileSource.Create(ClientServer.Current))
                .With<IGitSolarSource>(HttpGitSolarSource.Create(ClientServer.Current))
                .With<IColdArchiveClient>(HttpColdArchiveClient.Create(ClientServer.Current))
                .WithGitGui(ClientServer.Current)
                .WithCommunity(ClientServer.Current)
                // The head supplies the view context; a module with view documents renders them, others keep their code-built panes (doc 41c section 11).
                .WithViewContext(ViewContextServices.CreateDefault(viewActions, ex => Console.Error.WriteLine($"Bazaar: view action failed: {ex.Message}"))),
            terminals);
        features = ComposeMergeApproval(features);
        // Every module's manifest is checked the same way (doc 41 section 8); a module that fails adds nothing.
        var loaded = ClientModuleHost.Load(modules.OfType<IClientModule>().Append(new SupportBundleClientModule()), ClientHostFacts.Current(), Console.Error.WriteLine);
        var shipped = loaded.Only(modules);
        var showPanes = args.Contains("--panes", StringComparer.Ordinal);
        var showAthanor = args.Contains("--athanor", StringComparer.Ordinal);
        AthanorEditorServices? athanor = null;
        EditorThemeSync? themeSync = null;
        var athanorOptions = AthanorHeadOptions.FromEnvironment(Environment.GetEnvironmentVariable, AthanorHeadComposition.DefaultDataDirectory());
        var preloadStart = PreloadArguments.IsPreload(args);
        if (!preloadStart)
        {
            studio = StudioComposition.Start(
                ClientServer.Current,
                new FileCursorStore(CursorPath()),
                Environment.GetEnvironmentVariable(StudioComposition.PathVariable),
                Console.Error.WriteLine);
        }

        // The window is built on the UI thread. With chat settings it shows the dev chat; without them, the plain window.
        // With --panes it shows the Kanban and Constellation panes instead of the start content (chat keeps running).
        Window CreateMainWindow()
        {
            if (chatSettings is not null)
            {
                chat = ChatHost.Start(chatSettings, new FileCursorStore(CursorPath()), "windows", "bazaar-windows", action => Dispatcher.UIThread.Post(action), Console.Error.WriteLine);
            }

            // The registry is built here, on the UI thread, because the editor hosts are Avalonia controls (doc 57 section 2.2).
            // The editor is composed only for --athanor, so a normal start pays nothing for the WebView2 probe (doc 40 budgets).
            if (showAthanor)
            {
                athanor = AthanorHeadComposition.Create(athanorOptions, chatSettings, loaded.Commands, Console.Error.WriteLine);
                themeSync = AthanorHeadComposition.StartTheme(athanorOptions, athanor, Console.Error.WriteLine).Sync;
            }

            var registry = FirstPartyPanes.CreateRegistry(
                shipped,
                report: message => Console.Error.WriteLine($"Bazaar: {message}"),
                features: WithEditor(features, athanor));
            CalcinatoTerminalPanes.RegisterView(registry);

            // The warm process (doc 40) builds the window hidden; a forwarded launch shows it.
            var window = LauncherHost.HideWhenPreloading(chat is null ? new MainWindow() : new MainWindow(chat.View), preloadStart);
            if (FirstRunScreens.TryBuild(args) is { } firstRun)
            {
                window.Content = firstRun;
            }

            // The chat is the first slot of the posture layout and the panes the second, so a window of 600 dp or more shows both (doc 93).
            var chatView = window.Content as Control;
            Control? paneView = null;
            if (showAthanor)
            {
                window.ShowPanes([AthanorPaneModule.WindowPane], registry);
                paneView = window.Content as Control;
            }
            else if (showPanes)
            {
                window.ShowPanes([RyuseiFudaPanes.WindowPane, ConstellationPaneModule.WindowPane], registry);
                paneView = window.Content as Control;
            }

            // Mica or acrylic on the window chrome and a flat desktop posture; the chat and panes are content and stay opaque (doc 93).
            // The person's theme, materials, density and motion level come from the client settings file; the OS reduced-motion flag
            // and the frame-budget monitor feed the same state (doc 93, Settings and Behaviour).
            var settings = new ClientSettingsStore(VisualSettings.Definitions, SettingsPath(), Console.Error.WriteLine);
            var motion = new MotionSettings(MotionPlatform.Desktop);
            var materials = new WindowsMaterialEnvironmentSource();
            var visuals = ShellVisuals.Install(window, chatView, paneView, materials, new FlatPostureSource(), VisualSettings.LoadTheme(settings), motion);
            var reducedMotion = WindowsReducedMotion.Create();
            var binder = VisualSettingsBinder.Start(settings, materials.Settings, motion, reducedMotion, visuals.Materials, action => Dispatcher.UIThread.Post(action));
            window.Activated += (_, _) => reducedMotion.Refresh();
            window.Closed += (_, _) => binder.Dispose();


            paneSurface.Bind(
                registry.Contains,
                (kind, _) => Dispatcher.UIThread.InvokeAsync(() => window.ShowPanes([new PaneDescriptor(kind, kind, string.Empty, JsonElement.Parse("{}"))], registry)).GetTask());
            // Direction from the locale and the screen-reader live region wrap whatever the window now shows (doc 41c).
            ShellHost.Install(window, new SystemLocaleSource(), new AnnouncementGate(TimeProvider.System));

            // Keyboard mnemonics and the palette (doc 41). The person's remaps live beside the cursor file.
            window.Opened += async (_, _) =>
            {
                // The editor commands (edopen, ednew, edsave, eddiff) are typed text plus Enter, no Ctrl chord; with --athanor they run through the editor's dispatcher.
                var dispatcher = showAthanor ? AthanorHeadComposition.CreateDispatcher(window, athanorModule) : null;
                var head = await MnemonicHeadHost.AttachAsync(
                    window,
                    Path.Combine(Path.GetDirectoryName(CursorPath())!, "mnemonic-overrides.json"),
                    loaded.Commands,
                    dispatcher: dispatcher,
                    log: Console.Error.WriteLine);
                // "Save support bundle" (doc 37 section 4d): written off the UI thread, then the path goes to the client log.
                head.Shell.CommandInvoked += (_, id) =>
                {
                    if (id == SupportBundleClientModule.SaveCommandId)
                    {
                        _ = Task.Run(() => SupportBundleCommands.SaveFromPalette(modules, ClientDataPaths.DataDir(), TimeProvider.System, Console.Error.WriteLine));
                    }
                };
            };
            return window;
        }

        try
        {
            BuildAvaloniaApp(CreateMainWindow).StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            themeSync?.Dispose();
            chat?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            athanor?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            studio?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            terminals.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// Registers the merge identity and approver for the saved session (doc 52, Merge identity). Runs off the calling thread with a
    /// short budget; any failure, or no session, leaves both out and the merge card says this client cannot approve.
    /// </summary>
    private static PaneModuleContext ComposeMergeApproval(PaneModuleContext features)
    {
        try
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var whoAmI = new HttpWhoAmIClient(ClientServer.Current.CreateHttpClient());
            return MergeSessionResolver
                .ComposeAsync(features, ClientSessions.Store, whoAmI, WindowsDeviceApprovalPlatform.TryCreate(), cancellationToken: budget.Token)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception exception) when (exception is OperationCanceledException or InvalidOperationException or HttpRequestException)
        {
            Console.Error.WriteLine("Bazaar: merge approval is not available.");
            return features;
        }
    }

    /// <summary>
    /// The services the head hands the feature modules for the editor. Without <paramref name="athanor"/> the editor is the plain-text
    /// host over the offline source, which costs nothing at start; with it (<c>--athanor</c>) the editor is what
    /// <see cref="AthanorHeadComposition"/> chose (doc 57 section 2.2).
    /// </summary>
    internal static PaneModuleContext WithEditor(PaneModuleContext context, AthanorEditorServices? athanor) =>
        athanor is null
            ? context
                .With<IEditorHost>(new PlainTextFallbackHost())
                .With<IEditorFileSource>(new OfflineEditorFileSource())
                .With<IEditorDraftStore>(new FileEditorDraftStore(AthanorDraftsPath(), TimeProvider.System))
            : context
                .With<IEditorHost>(athanor.Host)
                .With<IEditorFileSource>(athanor.Source)
                .With<IEditorDraftStore>(athanor.Drafts);

    /// <summary>The services the head hands the feature modules, with the editor services from <see cref="WithEditor"/>.</summary>
    internal static PaneModuleContext CreateFeatures(IConstellationTileSource? tiles, AthanorEditorServices? athanor = null)
    {
        // The Git Solar System feed has no Client.Core type, so the head hands it over through PaneModuleContext.With.
        var context = WithEditor(new PaneModuleContext(), athanor)
            .With<IGitSolarSource>(HttpGitSolarSource.Create(ClientServer.Current))
            .WithGitGui(ClientServer.Current);
        return tiles is null ? context : context.With(tiles);
    }

    private static string AthanorDraftsPath() =>
        Path.Combine(AthanorHeadComposition.DefaultDataDirectory(), "athanor-drafts");

    /// <summary>
    /// Chat settings from the environment. <c>--dev-gc</c> overrides <c>BAZAAR_GC</c> for this run. Null means no chat is configured.
    /// </summary>
    private static ChatConnectionSettings? ChatSettings(IReadOnlyList<string> args)
    {
        var devGc = DevChatGcResolver.Resolve(args, cachedId: null);
        return ChatConnectionSettings.FromEnvironment(name =>
            name == ChatConnectionSettings.GcVariable && devGc is { } gc
                ? gc.ToString("D")
                : Environment.GetEnvironmentVariable(name));
    }

    private static string SettingsPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bazaar", "client-settings.json");
    private static string PreferencesPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bazaar", "preferences.json");

    private static string CursorPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bazaar", "cursors.json");

    private static AppBuilder BuildAvaloniaApp(Func<Window> createMainWindow) =>
        AppBuilder.Configure(() => new App(createMainWindow)).UsePlatformDetect();
}
