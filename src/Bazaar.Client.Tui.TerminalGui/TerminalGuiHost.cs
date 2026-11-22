using Terminal.Gui.App;
using Terminal.Gui.Time;

namespace Bazaar.Client.Tui.TerminalGui;

/// <summary>
/// Owns the one Terminal.Gui application of a TUI program and hands out its parts: the prompt widgets for the wizard and the shell for
/// the Ops screens. Created only when <see cref="TuiModeSelector"/> chose <see cref="TuiMode.FullScreen"/>; the plain wizard never
/// reaches this module, so the fallback path is untouched. Dispose it to restore the terminal.
/// </summary>
public sealed class TerminalGuiHost : IDisposable
{
    private readonly IApplication _application;

    private TerminalGuiHost(IApplication application)
    {
        _application = application;
        Widgets = new TerminalGuiPromptWidgets(application);
    }

    /// <summary>The widget port for the wizard prompter.</summary>
    public IPromptWidgets Widgets { get; }

    /// <summary>The application, for hosts that need its loop (timers, <c>Invoke</c>) and for tests that inject keys.</summary>
    public IApplication Application => _application;

    /// <summary>
    /// Starts the application. <paramref name="driverName"/> picks a Terminal.Gui driver (<c>ansi</c>, <c>dotnet</c> or <c>windows</c>);
    /// null takes the platform default. <paramref name="time"/> is a virtual clock for tests.
    /// </summary>
    public static TerminalGuiHost Start(string? driverName = null, ITimeProvider? time = null)
    {
        var application = Terminal.Gui.App.Application.Create(time);
        try
        {
            application.Init(driverName);
            return new TerminalGuiHost(application);
        }
        catch
        {
            application.Dispose();
            throw;
        }
    }

    /// <summary>Builds the shell for <paramref name="menus"/>, such as <see cref="DefaultMenus.Create"/>.</summary>
    public TuiShell CreateShell(IReadOnlyList<MenuModel> menus, ShellOptions? options = null) => new(_application, menus, options);

    public void Dispose() => _application.Dispose();
}
