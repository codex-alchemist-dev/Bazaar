using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Bazaar.Client.Components.Chat;
using Bazaar.Client.Components.Launcher;
using Bazaar.Client.Core;
using Bazaar.Client.Core.Chat;
using Bazaar.Client.Core.Wire;

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

        var chatSettings = ChatSettings(args);
        GcChatSession? chat = null;
        using var chatStop = new CancellationTokenSource();

        // The window is built on the UI thread. With chat settings it shows the dev chat; without them, the plain window.
        Window CreateMainWindow()
        {
            if (chatSettings is null)
            {
                return new MainWindow();
            }

            chat = ChatConnection.Create(chatSettings, new FileCursorStore(CursorPath()), "windows", "bazaar-windows");
            var view = new ChatViewModel(chat, action => Dispatcher.UIThread.Post(action));
            _ = chat.StartAsync(chatStop.Token).ContinueWith(
                task => Console.Error.WriteLine($"Bazaar: chat stopped: {task.Exception?.GetBaseException().Message}"),
                TaskContinuationOptions.OnlyOnFaulted);
            return new MainWindow(view);
        }

        try
        {
            BuildAvaloniaApp(CreateMainWindow).StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            chatStop.Cancel();
            chat?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            listenerStop.Cancel();
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

    private static void ActivateMainWindow()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
        {
            window.WindowState = WindowState.Normal;
            window.Activate();
        }
    }
}
