using Bazaar.Client.Calcinato.Avalonia;
using Bazaar.Client.Calcinato.Pty;
using Bazaar.Client.Calcinato.Session;
using Bazaar.Client.Calcinato.Sources;
using Bazaar.Client.Panes.Abstractions;

namespace Bazaar.Client.Linux;

/// <summary>Composes the Calcinato terminal pane into this head's pane registry (doc 55, section 5.4).</summary>
public static class CalcinatoTerminalPanes
{
    /// <summary>The session manager for this head: the OS PTY backend, with remote attach not available yet.</summary>
    public static TerminalSessionManager CreateSessions() =>
        new(new TerminalHost(PtyBackendSelector.CreateDefault(), new UnavailableRemoteTerminalHub()), TimeProvider.System);

    /// <summary>
    /// Composes <paramref name="modules"/> with the session manager and clock in the context, then registers the terminal
    /// control as the view of the terminal pane.
    /// </summary>
    /// <param name="viewFactory">Builds the generic fallback view and the title chrome.</param>
    /// <param name="modules">The pane modules the head ships, including <c>CalcinatoPanes.Modules</c>.</param>
    /// <param name="context">The head's context (tiles, report). The terminal services are added to it.</param>
    /// <param name="sessions">The session manager the terminal pane follows.</param>
    public static PaneRegistry Compose(IPaneViewFactory viewFactory, IEnumerable<IPaneModule> modules, PaneModuleContext context, TerminalSessionManager sessions)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(sessions);

        var registry = PaneRegistry.Compose(viewFactory, modules, context.With(sessions).With(TimeProvider.System));
        TerminalPaneViews.Register(registry);
        return registry;
    }
}
