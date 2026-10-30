using Avalonia;
using Avalonia.Threading;
using Bazaar.Client.Core;
using Bazaar.Client.Core.Chat;
using Bazaar.Client.Core.Wire;

namespace Bazaar.Client.Linux;

internal static class Program
{
    private const string _clientBuild = "bazaar-linux";

    [STAThread]
    public static int Main(string[] args)
    {
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
        return BuildAvaloniaApp(() => CreateMainWindow(settings, paths)).StartWithClassicDesktopLifetime(args);
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
