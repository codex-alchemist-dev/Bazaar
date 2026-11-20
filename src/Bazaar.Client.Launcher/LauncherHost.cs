using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Bazaar.Client.Components.Launcher;

namespace Bazaar.Client.Launcher;

/// <summary>
/// The Avalonia side of the single-instance launcher (doc 28, doc 40): listens for forwarded launches and brings the main
/// window forward, and hides the window of the warm preload process. The pure pipe and argument parsing stay in
/// <c>Bazaar.Client.Components.Launcher</c>; this keeps the window glue in one place for every desktop head.
/// </summary>
public sealed class LauncherHost : IDisposable
{
    private readonly CancellationTokenSource _stop = new();

    private LauncherHost(string pipeName, Action<string> log)
    {
        // TODO(component): pane routing for --open and --terminal lands with the pane host; for now every
        // forwarded launch only brings the main window forward.
        _ = LaunchPipe.ListenAsync(pipeName, _ => Dispatcher.UIThread.InvokeAsync(ActivateMainWindow).GetTask(), _stop.Token)
            .ContinueWith(
                task => log($"Bazaar: launcher listener stopped: {task.Exception?.GetBaseException().Message}"),
                TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>Starts listening on the pipe. Dispose the host to stop.</summary>
    /// <param name="pipeName">The per-user pipe name.</param>
    /// <param name="log">Receives one line if the listener stops with a failure.</param>
    public static LauncherHost Start(string pipeName, Action<string> log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentNullException.ThrowIfNull(log);
        return new LauncherHost(pipeName, log);
    }

    /// <summary>Shows the main window if hidden, restores it and activates it. Does nothing without a desktop lifetime. Call on the UI thread.</summary>
    public static void ActivateMainWindow()
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

    /// <summary>
    /// For the warm process (doc 40): hides the window as soon as it opens when <paramref name="preload"/> is true. A later
    /// forwarded launch shows it through <see cref="ActivateMainWindow"/>.
    /// </summary>
    public static T HideWhenPreloading<T>(T window, bool preload)
        where T : Window
    {
        ArgumentNullException.ThrowIfNull(window);
        if (preload)
        {
            window.Opened += (_, _) => window.Hide();
        }

        return window;
    }

    /// <summary>Stops the listener.</summary>
    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}
