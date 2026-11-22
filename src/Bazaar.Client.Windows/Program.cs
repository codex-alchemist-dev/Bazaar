using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Bazaar.Accessibility;
using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Abstractions.Server;
using Bazaar.Client.Abstractions.Wire;
using Bazaar.Client.Calcinato.Panes;
using Bazaar.Client.Calendar;
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
using Bazaar.Client.Interrupts;
using Bazaar.Client.Launcher;
using Bazaar.Client.Modules;
using Bazaar.Client.Org;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Client.Preload;
using Bazaar.Client.RyuseiFuda;
using Bazaar.Client.Shell;
using Bazaar.Client.Vault;
using Bazaar.Contracts.Modules;
using Bazaar.Ex.Head;
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
        IPaneModule[] modules =
        [
            RyuseiFudaPanes.Board,
            new DashboardPaneModule(),
            new ConstellationPaneModule(),
            new GcPaneModule(),
            new VaultSearchPaneModule(),
            .. RyuseiFudaPanes.Modules,
            .. OrgPanes.Modules,
            .. CalendarPanes.Modules,
            .. InterruptPanes.Modules,
            new EnginePaneModule(),
            .. GitSolarSystemPanes.Modules,
            .. GitGuiPanes.Modules,
            // The terminal pane comes last in pager order (doc 55, section 7).
            .. CalcinatoPanes.Modules,
        ];
        var terminals = CalcinatoTerminalPanes.CreateSessions();
        // The Git Solar System feed has no Client.Core type, so the head hands it over through PaneModuleContext.With.
        var features = CalcinatoTerminalPanes.AddServices(
            new PaneModuleContext()
                .With<IConstellationTileSource>(HttpConstellationTileSource.Create(ClientServer.Current))
                .With<IGitSolarSource>(HttpGitSolarSource.Create(ClientServer.Current))
                .WithGitGui(ClientServer.Current),
            terminals);
        // Every module's manifest is checked the same way (doc 41 section 8); a module that fails adds nothing.
        var loaded = ClientModuleHost.Load(modules.OfType<IClientModule>(), ClientHostFacts.Current(), Console.Error.WriteLine);
        var shipped = loaded.Only(modules);
        var registry = FirstPartyPanes.CreateRegistry(
            shipped,
            report: message => Console.Error.WriteLine($"Bazaar: {message}"),
            features: features);
        CalcinatoTerminalPanes.RegisterView(registry);
        var showPanes = args.Contains("--panes", StringComparer.Ordinal);
        var preloadStart = PreloadArguments.IsPreload(args);

        // The window is built on the UI thread. With chat settings it shows the dev chat; without them, the plain window.
        // With --panes it shows the Kanban and Constellation panes instead of the start content (chat keeps running).
        Window CreateMainWindow()
        {
            if (chatSettings is not null)
            {
                chat = ChatHost.Start(chatSettings, new FileCursorStore(CursorPath()), "windows", "bazaar-windows", action => Dispatcher.UIThread.Post(action), Console.Error.WriteLine);
            }

            // The warm process (doc 40) builds the window hidden; a forwarded launch shows it.
            var window = LauncherHost.HideWhenPreloading(chat is null ? new MainWindow() : new MainWindow(chat.View), preloadStart);
            if (FirstRunScreens.TryBuild(args) is { } firstRun)
            {
                window.Content = firstRun;
            }

            if (showPanes)
            {
                window.ShowPanes([RyuseiFudaPanes.WindowPane, ConstellationPaneModule.WindowPane], registry);
            }

            // Direction from the locale and the screen-reader live region wrap whatever the window now shows (doc 41c).
            ShellHost.Install(window, new SystemLocaleSource(), new AnnouncementGate(TimeProvider.System));

            // Keyboard mnemonics and the palette (doc 41). The person's remaps live beside the cursor file.
            window.Opened += async (_, _) => await MnemonicHeadHost.AttachAsync(window, Path.Combine(Path.GetDirectoryName(CursorPath())!, "mnemonic-overrides.json"), loaded.Commands);
            return window;
        }

        try
        {
            BuildAvaloniaApp(CreateMainWindow).StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            chat?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            terminals.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

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
