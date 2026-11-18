using Bazaar.Client.Calcinato.Session;
using Bazaar.Client.Core.Panes;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Calcinato.Panes;

/// <summary>
/// Registers the terminal pane (<see cref="PaneKinds.Terminal"/>). The head puts its <see cref="TerminalSessionManager"/>
/// in the context with <see cref="PaneModuleContext.With{T}"/>, and an optional <see cref="TimeProvider"/> the same way.
/// Without a manager the pane shows its empty state.
/// </summary>
/// <remarks>
/// No view is registered here. <see cref="PaneRegistry.RegisterView{TPane}"/> takes a view function, not a view id, and
/// the Avalonia control belongs to the head adapter (doc 55, section 5.4). Heads register it with
/// <c>TerminalPaneViews.Register</c> from Bazaar.Client.Calcinato.Avalonia after composing the registry.
/// </remarks>
public sealed class TerminalPaneModule : IPaneModule
{
    /// <inheritdoc />
    public string Id => PaneKinds.Terminal;

    /// <inheritdoc />
    public void Register(PaneRegistry registry, PaneModuleContext context)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(context);

        TerminalSessionManager? manager = context.Get<TerminalSessionManager>();
        TimeProvider time = context.Get<TimeProvider>() ?? TimeProvider.System;
        registry.Register(PaneKinds.Terminal, descriptor => new TerminalPaneViewModel(descriptor.Title, manager, time));
    }
}
