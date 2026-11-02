using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Bazaar.Client.Components.Launcher;
using Bazaar.Client.Core;

namespace Bazaar.Client.Windows;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
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

        using var listenerStop = new CancellationTokenSource();
        if (isPrimary)
        {
            // TODO(component): pane routing for --open and --terminal lands with the pane host; for now every
            // forwarded launch only brings the main window forward.
            _ = LaunchPipe.ListenAsync(pipeName, _ => Dispatcher.UIThread.InvokeAsync(ActivateMainWindow).GetTask(), listenerStop.Token)
                .ContinueWith(
                    task => Console.Error.WriteLine($"Bazaar: launcher listener stopped: {task.Exception?.GetBaseException().Message}"),
                    TaskContinuationOptions.OnlyOnFaulted);
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            listenerStop.Cancel();
        }
    }

    private static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect();

    private static void ActivateMainWindow()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
        {
            window.WindowState = WindowState.Normal;
            window.Activate();
        }
    }
}
