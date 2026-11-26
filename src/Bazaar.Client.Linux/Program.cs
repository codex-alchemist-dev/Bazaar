using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Bazaar.Accessibility;
using Bazaar.Client.Abstractions.Wire;
using Bazaar.Client.Chat;
using Bazaar.Client.Commands;
using Bazaar.Client.Components.Launcher;
using Bazaar.Client.Core;
using Bazaar.Client.Launcher;
using Bazaar.Client.Linux.Display;
using Bazaar.Client.Linux.Updates;
using Bazaar.Client.Preload;
using Bazaar.Client.Shell;
using Bazaar.Ex.Head;
using Bazaar.Localisation;
using Bazaar.SupportBundle;

namespace Bazaar.Client.Linux;

internal static class Program
{
    // The router modules register command handlers into. This head loads no pane modules yet.
    private static readonly CommandRouter _router = new();

    private const string _clientBuild = "bazaar-linux";

    [STAThread]
    public static int Main(string[] args)
    {
        if (DefaultAppCommandLine.TryRun(args, Environment.ProcessPath, Console.Out, Console.Error) is { } defaultAppExit) { return defaultAppExit; }
        if (DefaultTerminalCommandLine.TryRun(args, Environment.ProcessPath, Console.Out, Console.Error) is { } defaultTerminalExit) { return defaultTerminalExit; }
        // Client GC: sustained low latency keeps UI frames free of blocking gen2 collections (doc 40).
        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
        ChatConnectionSettings? settings;
        try
        {
            settings = LinuxChatSettings.Resolve(
                args,
                Environment.GetEnvironmentVariable,
                LinuxPaths.FromEnvironment(Environment.GetEnvironmentVariable, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
                LinuxClientSessions.Store,
                CurrentEndpoint,
                TimeProvider.System,
                LinuxClientSessions.RefreshStore);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        // Headless modes (doc 37 section 4d) run before the single-instance forward and any window.
        if (SupportBundleCommands.TryRun(args, ClientDataPaths.DataDir(Environment.GetEnvironmentVariable)) is { } headlessExit)
        {
            return headlessExit;
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
        // One background update check shortly after start; it fails closed without a pinned key (doc 30 section 7).
        using var updates = UpdateHost.Start(Environment.GetEnvironmentVariable, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Console.Error.WriteLine);
        var backend = DisplayBackendSelector.Select(Environment.GetEnvironmentVariable);
        return BuildAvaloniaApp(() => LauncherHost.HideWhenPreloading(CreateMainWindow(settings, paths, args, updates), preloadStart), backend).StartWithClassicDesktopLifetime(args);
        int exitCode;
        using (var launcher = LauncherHost.Start(pipeName, Console.Error.WriteLine))
        {
            // One background update check shortly after start; it fails closed without a pinned key (doc 30 section 7).
            using var updates = UpdateHost.Start(Environment.GetEnvironmentVariable, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Console.Error.WriteLine);
            args = [.. LinuxClientSessions.WithAutoSignIn(args, settings is null, preloadStart)];
            var backend = DisplayBackendSelector.Select(Environment.GetEnvironmentVariable);
            exitCode = BuildAvaloniaApp(() => { LinuxTheme.ApplyAtStart(args, Environment.GetEnvironmentVariable, Console.Error.WriteLine); return LauncherHost.HideWhenPreloading(CreateMainWindow(settings, paths, args, updates), preloadStart); }, backend).StartWithClassicDesktopLifetime(args);
        }

        // "Restart to apply" (doc 30 section 7): the launcher pipe is released above, so the new process starts as the primary instance.
        PendingRestart.StartIfScheduled(Console.Error.WriteLine);
        return exitCode;
    }

    /// <summary>The endpoint this build was made for (doc 37 section 4a), or null when the build has none (the head then opens no chat).</summary>
    private static Bazaar.Client.Abstractions.Server.ServerEndpoint? CurrentEndpoint()
    {
        try
        {
            Bazaar.Client.Abstractions.Server.ClientServer.ConnectToCompanyServer();
            return Bazaar.Client.Abstractions.Server.ClientServer.Current;
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"Bazaar: {ex.Message}");
            return null;
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
    /// <summary>
    /// Builds the window through the pane host (doc 30 section 1). The dev chat settings from the environment still start a chat,
    /// which the GC pane shows; with none, the window opens on the Dashboard. <c>--terminal</c>, <c>--sign-in</c>,
    /// <c>--create-company</c> and <c>--first-run</c> keep working.
    /// </summary>
    private static MainWindow CreateMainWindow(ChatConnectionSettings? settings, LinuxPaths paths, IReadOnlyList<string> args, UpdateHost updates)
    {
        if (settings is null)
        {
            Console.Error.WriteLine($"Not connected. Set {ChatConnectionSettings.ServerVariable}, {ChatConnectionSettings.GcVariable} and {ChatConnectionSettings.TokenVariable}.");
            var plain = new MainWindow();
            ShowFirstRun(plain, args);
            InstallShell(plain);
            return plain;
            Console.Error.WriteLine($"No chat to open yet. Sign in, then start once with {LinuxChatSettings.DevGcFlag} <group chat id> (it is remembered), or set {ChatConnectionSettings.ServerVariable}, {ChatConnectionSettings.GcVariable} and {ChatConnectionSettings.TokenVariable}.");
        }

        var dataDir = ClientDataPaths.DataDir(Environment.GetEnvironmentVariable);
        return LinuxHead.Create(new LinuxHeadOptions(settings, paths, args, updates, dataDir, _clientBuild, Console.Error.WriteLine)).Window;
    }

    // --sign-in, --create-company and --first-run replace the window content with the first-run screens (doc 45 section 2.6).
    private static void ShowFirstRun(MainWindow window, IReadOnlyList<string> args)
    {
        if (FirstRunScreens.TryBuild(args) is { } firstRun)
        {
            window.Content = firstRun;
        }
    }

    // Keyboard mnemonics and the palette (doc 41). The person's remaps live beside the other state files.
    private static void AttachMnemonics(MainWindow window, LinuxPaths paths)
    {
        Directory.CreateDirectory(paths.StateDirectory);
        var chat = ChatHost.Start(settings, new FileCursorStore(paths.CursorFile), "linux", _clientBuild, action => Dispatcher.UIThread.Post(action), Console.Error.WriteLine, updates.OnWelcome);
        var window = new MainWindow(chat.View);
        ShowFirstRun(window, args);
        window.Closed += async (_, _) => await chat.DisposeAsync();
        InstallShell(window);
        AttachMnemonics(window, paths);
        return window;
        window.Opened += async (_, _) =>
        {
            var host = await MnemonicHeadHost.AttachAsync(window, Path.Combine(paths.StateDirectory, "mnemonic-overrides.json"));
            // What a chosen command does belongs to the pane module that registered a handler for it; this head loads none yet.
            host.Shell.CommandInvoked += (_, id) =>
            {
                if (!_router.TryInvoke(id))
                {
                    Console.Error.WriteLine($"Bazaar: no pane handles mnemonic command {id}");
                }
            };
        };
    }

    // `--terminal` shows the local terminal pane in place of the start content (doc 55, section 5.4).
    private static void ShowTerminalWhenAsked(MainWindow window, IReadOnlyList<string> args)
    {
        if (!args.Contains("--terminal", StringComparer.Ordinal))
        {
            return;
        }

        var sessions = CalcinatoTerminalPanes.CreateSessions();
        CalcinatoTerminalPanes.ShowTerminal(window, sessions);
        window.Closed += async (_, _) => await sessions.DisposeAsync();
    }

    // --sign-in, --create-company and --first-run replace the window content with the first-run screens (doc 45 section 2.6).
    private static void ShowFirstRun(MainWindow window, IReadOnlyList<string> args)
    {
        if (FirstRunScreens.TryBuild(args) is { } firstRun)
        {
            window.Content = firstRun;
        }
    }

    // Keyboard mnemonics and the palette (doc 41). The person's remaps live beside the other state files.
    private static void AttachMnemonics(MainWindow window, LinuxPaths paths)
    {
        Directory.CreateDirectory(paths.StateDirectory);
        window.Opened += async (_, _) => await MnemonicHeadHost.AttachAsync(window, Path.Combine(paths.StateDirectory, "mnemonic-overrides.json"));
    }

    // Direction from the locale and the screen-reader live region wrap whatever the window shows (doc 41c).
    private static void InstallShell(MainWindow window) =>
        ShellHost.Install(window, new SystemLocaleSource(), new AnnouncementGate(TimeProvider.System));
}
