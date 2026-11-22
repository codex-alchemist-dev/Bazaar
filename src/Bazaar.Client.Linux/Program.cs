using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Bazaar.Accessibility;
using Bazaar.Client.Abstractions.Wire;
using Bazaar.Client.Chat;
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

    /// <summary>
    /// Builds the window through the pane host (doc 30 section 1). The dev chat settings from the environment still start a chat,
    /// which the GC pane shows; with none, the window opens on the Dashboard. <c>--terminal</c>, <c>--sign-in</c>,
    /// <c>--create-company</c> and <c>--first-run</c> keep working.
    /// </summary>
    private static MainWindow CreateMainWindow(ChatConnectionSettings? settings, LinuxPaths paths, IReadOnlyList<string> args, UpdateHost updates)
    {
        if (settings is null)
        {
            Console.Error.WriteLine($"No chat to open yet. Sign in, then start once with {LinuxChatSettings.DevGcFlag} <group chat id> (it is remembered), or set {ChatConnectionSettings.ServerVariable}, {ChatConnectionSettings.GcVariable} and {ChatConnectionSettings.TokenVariable}.");
        }

        var dataDir = ClientDataPaths.DataDir(Environment.GetEnvironmentVariable);
        return LinuxHead.Create(new LinuxHeadOptions(settings, paths, args, updates, dataDir, _clientBuild, Console.Error.WriteLine)).Window;
    }
}
