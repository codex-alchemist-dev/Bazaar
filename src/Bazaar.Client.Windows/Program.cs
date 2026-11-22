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
using Bazaar.Client.Chat;
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
using Bazaar.Client.Launcher;
using Bazaar.Client.Modules;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Client.Preload;
using Bazaar.Client.RyuseiFuda;
using Bazaar.Client.Shell;
using Bazaar.Client.Vault;
using Bazaar.Contracts.Modules;
using Bazaar.Localisation;

namespace Bazaar.Client.Windows;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Client GC: sustained low latency keeps UI frames free of blocking gen2 collections (doc 40).
        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
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

        // Panes come from the first-party registry; the Constellation tiles come from the company server this build was made for (doc 37 section 4a).
        ClientServer.ConnectToCompanyServer();
        // The Dashboard comes first, then Constellation, then the GC pane, then Vault, then the rest of RyuseiFuda, then Git Solar System (pager order).
        var athanorModule = new AthanorPaneModule();
        IPaneModule[] modules =
        [
            RyuseiFudaPanes.Board,
            new DashboardPaneModule(),
            new ConstellationPaneModule(),
            new GcPaneModule(),
            new VaultSearchPaneModule(),
            .. RyuseiFudaPanes.Modules,
            new EnginePaneModule(),
            athanorModule,
            .. GitSolarSystemPanes.Modules,
            .. GitGuiPanes.Modules,
        ];
        var loadResult = ClientModuleHost.Load(modules.OfType<IClientModule>(), ClientHostFacts.Current(), Console.Error.WriteLine);
        var shipped = loadResult.Only(modules);
        var showPanes = args.Contains("--panes", StringComparer.Ordinal);
        var showAthanor = args.Contains("--athanor", StringComparer.Ordinal);
        var preloadStart = PreloadArguments.IsPreload(args);
        AthanorEditorServices? athanor = null;
        var athanorOptions = AthanorHeadOptions.FromEnvironment(Environment.GetEnvironmentVariable, AthanorHeadComposition.DefaultDataDirectory());
        Bazaar.Ex.Head.MnemonicHeadHost? commands = null;
        EditorThemeSync? themeSync = null;

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
                athanor = AthanorHeadComposition.Create(
                    athanorOptions,
                    chatSettings,
                    loadResult.Commands,
                    Console.Error.WriteLine);
                themeSync = AthanorHeadComposition.StartTheme(athanorOptions, athanor, Console.Error.WriteLine).Sync;
            }

            var features = CreateFeatures(HttpConstellationTileSource.Create(ClientServer.Current), athanor);
            var registry = FirstPartyPanes.CreateRegistry(
                shipped,
                report: message => Console.Error.WriteLine($"Bazaar: {message}"),
                features: features);

            // The warm process (doc 40) builds the window hidden; a forwarded launch shows it.
            var window = LauncherHost.HideWhenPreloading(chat is null ? new MainWindow() : new MainWindow(chat.View), preloadStart);
            if (FirstRunScreens.TryBuild(args) is { } firstRun)
            {
                window.Content = firstRun;
            }

            if (showAthanor)
            {
                window.ShowPanes([AthanorPaneModule.WindowPane], registry);
                // The editor commands (edopen, ednew, edsave, eddiff) are reached by mnemonic: typed text plus Enter, no Ctrl chord.
                commands = AthanorHeadComposition.StartCommands(window, loadResult.Commands, athanorModule, athanorOptions, Console.Error.WriteLine);
            }
            else if (showPanes)
            {
                window.ShowPanes([RyuseiFudaPanes.WindowPane, ConstellationPaneModule.WindowPane], registry);
            }

            // Direction from the locale and the screen-reader live region wrap whatever the window now shows (doc 41c).
            ShellHost.Install(window, new SystemLocaleSource(), new AnnouncementGate(TimeProvider.System));
            return window;
        }

        try
        {
            BuildAvaloniaApp(CreateMainWindow).StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            commands?.Dispose();
            themeSync?.Dispose();
            chat?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            athanor?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// The services the head hands the feature modules. Without <paramref name="athanor"/> the editor is the plain-text host over
    /// the offline source, which costs nothing at start; with it (<c>--athanor</c>) the editor is what
    /// <see cref="AthanorHeadComposition"/> chose (doc 57 section 2.2).
    /// </summary>
    internal static PaneModuleContext CreateFeatures(IConstellationTileSource? tiles, AthanorEditorServices? athanor = null)
    {
        var context = athanor is null
            ? new PaneModuleContext()
                .With<IEditorHost>(new PlainTextFallbackHost())
                .With<IEditorFileSource>(new OfflineEditorFileSource())
                .With<IEditorDraftStore>(new FileEditorDraftStore(AthanorDraftsPath(), TimeProvider.System))
            : new PaneModuleContext()
                .With<IEditorHost>(athanor.Host)
                .With<IEditorFileSource>(athanor.Source)
                .With<IEditorDraftStore>(athanor.Drafts);
        // The Git Solar System feed has no Client.Core type, so the head hands it over through PaneModuleContext.With.
        context = context.With<IGitSolarSource>(HttpGitSolarSource.Create(ClientServer.Current)).WithGitGui(ClientServer.Current);
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

    private static string CursorPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bazaar", "cursors.json");

    private static AppBuilder BuildAvaloniaApp(Func<Window> createMainWindow) =>
        AppBuilder.Configure(() => new App(createMainWindow)).UsePlatformDetect();
}
