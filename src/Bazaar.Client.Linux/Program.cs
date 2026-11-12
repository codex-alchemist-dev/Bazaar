using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Bazaar.Client.Components.Launcher;
using Bazaar.Client.Core;
using Bazaar.Client.Core.Chat;
using Bazaar.Client.Core.Preload;
using Bazaar.Client.Core.Wire;

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

        using var listenerStop = new CancellationTokenSource();
        _ = LaunchPipe.ListenAsync(pipeName, _ => Dispatcher.UIThread.InvokeAsync(ActivateMainWindow).GetTask(), listenerStop.Token)
            .ContinueWith(
                task => Console.Error.WriteLine($"Bazaar: launcher listener stopped: {task.Exception?.GetBaseException().Message}"),
                TaskContinuationOptions.OnlyOnFaulted);

        try
        {
            return BuildAvaloniaApp(() => HideIfPreloading(CreateMainWindow(settings, paths), preloadStart)).StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            listenerStop.Cancel();
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

    private static void ActivateMainWindow()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
        {
            if (!window.IsVisible)
            {
                window.Show();
            }

            window.WindowState = WindowState.Normal;
            window.Activate();
        }
    }

    private static MainWindow HideIfPreloading(MainWindow window, bool preloadStart)
    {
        if (preloadStart)
        {
            window.Opened += (_, _) => window.Hide();
        }

        return window;
    }

    private static AppBuilder BuildAvaloniaApp(Func<MainWindow> createMainWindow) =>
        AppBuilder.Configure(() => new App(createMainWindow)).UsePlatformDetect();

    /// <summary>Builds the window. With settings, the window shows the group chat and the session starts; without, it opens empty.</summary>
    private static MainWindow CreateMainWindow(ChatConnectionSettings? settings, LinuxPaths paths)
    {
        if (settings is null)
        {
            Console.Error.WriteLine($"Not connected. Set {ChatConnectionSettings.ServerVariable}, {ChatConnectionSettings.GcVariable} and {ChatConnectionSettings.TokenVariable}.");
            return new MainWindow();
        }

        Directory.CreateDirectory(paths.StateDirectory);
        GcChatSession session = ChatConnection.Create(settings, new FileCursorStore(paths.CursorFile), "linux", _clientBuild);
        var chat = new ChatViewModel(session, action => Dispatcher.UIThread.Post(action));
        var window = new MainWindow(chat);

        var cts = new CancellationTokenSource();
        window.Closed += async (_, _) =>
        {
            chat.Dispose();
            await session.DisposeAsync();
            cts.Dispose();
        };

        _ = StartSessionAsync(session, cts.Token);
        return window;
    }

    private static async Task StartSessionAsync(GcChatSession session, CancellationToken cancellationToken)
    {
        try
        {
            await session.StartAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"Chat failed to start: {ex.Message}");
        }
    }
}
