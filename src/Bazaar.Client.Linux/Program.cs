using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Bazaar.Accessibility;
using Bazaar.Client.Abstractions.Wire;
using Bazaar.Client.Athanor;
using Bazaar.Client.Athanor.Hosting;
using Bazaar.Client.Athanor.Pane;
using Bazaar.Client.Chat;
using Bazaar.Client.Components.Launcher;
using Bazaar.Client.Core;
using Bazaar.Client.Core.Panes;
using Bazaar.Client.Launcher;
using Bazaar.Client.Linux.Display;
using Bazaar.Client.Modules;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Client.Preload;
using Bazaar.Client.Shell;
using Bazaar.Contracts.Modules;
using Bazaar.Localisation;

namespace Bazaar.Client.Linux;

internal static class Program
{
    private const string _clientBuild = "bazaar-linux";

    [STAThread]
    public static int Main(string[] args)
    {
        // Client GC: sustained low latency keeps UI frames free of blocking gen2 collections (doc 40).
        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
        ChatConnectionSettings? settings;
        try
        {
            settings = ChatConnectionSettings.FromEnvironment(Environment.GetEnvironmentVariable);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        var paths = LinuxPaths.FromEnvironment(Environment.GetEnvironmentVariable, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        // The warm process (doc 40) builds its window hidden; the window stays hidden until a forwarded launch shows it.
        var preloadStart = PreloadArguments.IsPreload(args);

        // One instance per user (doc 40): a launch that finds a running instance (warm or visible) forwards and exits.
        // The running instance shows its window when a forwarded launch arrives.
        var pipeName = LaunchPipe.DefaultName(Environment.UserName);
        var request = LaunchArgs.Parse(args);
        if (TryForward(pipeName, request))
        {
            return 0;
        }

        using var launcher = LauncherHost.Start(pipeName, Console.Error.WriteLine);
        var backend = DisplayBackendSelector.Select(Environment.GetEnvironmentVariable);
        var showAthanor = args.Contains("--athanor", StringComparer.Ordinal);
        AthanorEditorServices? athanor = null;
        try
        {
            return BuildAvaloniaApp(() => LauncherHost.HideWhenPreloading(CreateMainWindow(settings, paths, showAthanor, services => athanor = services), preloadStart), backend).StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            athanor?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>True when a running instance accepted the request. False when none is listening (start as primary).</summary>
    private static bool TryForward(string pipeName, LaunchRequest request)
    {
        try
        {
            // A short budget: with no running instance the connect fails at once, so a cold start pays almost nothing.
            return LaunchPipe.TryForwardAsync(pipeName, request, TimeSpan.FromMilliseconds(300), CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Builds the Avalonia app on the backend the session asks for (doc 30, section 1). An explicit X11 or Wayland choice is
    /// honoured; with no display detected, platform detection runs unchanged so the failure stays Avalonia's own.
    /// </summary>
    private static AppBuilder BuildAvaloniaApp(Func<MainWindow> createMainWindow, DisplayBackend backend)
    {
        // Skia renders and HarfBuzz shapes text (the test app builder uses Skia too); without either, Avalonia refuses to start.
        var builder = AppBuilder.Configure(() => new App(createMainWindow)).UseSkia().UseHarfBuzz();
        switch (backend)
        {
            case DisplayBackend.X11:
                return builder.UseX11();
            case DisplayBackend.Wayland:
                return builder.UseWayland();
            default:
                return builder.UsePlatformDetect();
        }
    }

    /// <summary>Builds the window. With settings, the window shows the group chat and the session starts; without, it opens empty.</summary>
    private static MainWindow CreateMainWindow(ChatConnectionSettings? settings, LinuxPaths paths, bool showAthanor, Action<AthanorEditorServices> keepAthanor)
    {
        MainWindow window;
        if (settings is null)
        {
            Console.Error.WriteLine($"Not connected. Set {ChatConnectionSettings.ServerVariable}, {ChatConnectionSettings.GcVariable} and {ChatConnectionSettings.TokenVariable}.");
            window = new MainWindow();
        }
        else
        {
            Directory.CreateDirectory(paths.StateDirectory);
            var chat = ChatHost.Start(settings, new FileCursorStore(paths.CursorFile), "linux", _clientBuild, action => Dispatcher.UIThread.Post(action), Console.Error.WriteLine);
            window = new MainWindow(chat.View);
            window.Closed += async (_, _) => await chat.DisposeAsync();
        }

        if (showAthanor)
        {
            ShowAthanor(window, settings, paths, keepAthanor);
        }

        // Installed last so it wraps whatever the window now shows.
        InstallShell(window);
        return window;
    }

    /// <summary>
    /// Shows the editor pane (<c>--athanor</c>). The editor is composed here, on the UI thread, because its hosts are Avalonia
    /// controls and so that a normal start pays nothing for it (doc 57 section 2.2).
    /// </summary>
    private static void ShowAthanor(MainWindow window, ChatConnectionSettings? settings, LinuxPaths paths, Action<AthanorEditorServices> keepAthanor)
    {
        var athanorModule = new AthanorPaneModule();
        IClientModule[] modules = [athanorModule];
        var load = ClientModuleHost.Load(modules, ClientHostFacts.Current(), Console.Error.WriteLine);
        var options = AthanorHeadOptions.FromEnvironment(Environment.GetEnvironmentVariable, paths.StateDirectory);
        var athanor = AthanorHeadComposition.Create(
            options,
            settings,
            load.Commands,
            Console.Error.WriteLine);
        keepAthanor(athanor);
        var features = new PaneModuleContext()
            .With<IEditorHost>(athanor.Host)
            .With<IEditorFileSource>(athanor.Source)
            .With<IEditorDraftStore>(athanor.Drafts);
        var registry = FirstPartyPanes.CreateRegistry(
            load.Only<IPaneModule>(modules.OfType<IPaneModule>()),
            report: message => Console.Error.WriteLine($"Bazaar: {message}"),
            features: features);
        window.ShowPanes([AthanorPaneModule.WindowPane], registry);

        // The editor commands (edopen, ednew, edsave, eddiff) are reached by mnemonic: typed text plus Enter, no Ctrl chord.
        var commands = AthanorHeadComposition.StartCommands(window, load.Commands, athanorModule, options, Console.Error.WriteLine);
        window.Closed += (_, _) => commands?.Dispose();
    }

    // Direction from the locale and the screen-reader live region wrap whatever the window shows (doc 41c).
    private static void InstallShell(MainWindow window) =>
        ShellHost.Install(window, new SystemLocaleSource(), new AnnouncementGate(TimeProvider.System));
}
