using Bazaar.Client.Panes.Abstractions;

namespace Bazaar.Client.Calcinato.Avalonia;

/// <summary>
/// The Avalonia body of the terminal pane. A head calls <see cref="Register"/> on the registry it composed from
/// <see cref="Bazaar.Client.Calcinato.Panes.CalcinatoPanes.Modules"/>, the same way the RyuseiFuda pane views register.
/// </summary>
public static class TerminalPaneViews
{
    /// <summary>Registers the terminal control as the view of the terminal pane. Throws when a view is already registered.</summary>
    /// <param name="registry">The registry the head composed.</param>
    public static void Register(PaneRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.RegisterView<Bazaar.Client.Calcinato.Panes.TerminalPaneViewModel>(Create);
    }

    /// <summary>Builds the terminal control for a pane.</summary>
    /// <param name="pane">The pane to draw.</param>
    public static TerminalControl Create(Bazaar.Client.Calcinato.Panes.TerminalPaneViewModel pane) => new(pane);
}
