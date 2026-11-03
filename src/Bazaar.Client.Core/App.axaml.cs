using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Bazaar.Client.Core;

public sealed class App : Application
{
    private readonly Func<Window>? _createMainWindow;

    public App() : this(null)
    {
    }

    /// <param name="createMainWindow">Builds the main window. Heads pass one that wires a chat; null uses the plain window.</param>
    public App(Func<Window>? createMainWindow)
    {
        _createMainWindow = createMainWindow;
    }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = _createMainWindow?.Invoke() ?? new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
