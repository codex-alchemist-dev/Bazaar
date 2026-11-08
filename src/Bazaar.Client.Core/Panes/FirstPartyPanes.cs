using Bazaar.Client.Core.Chat;
using Bazaar.Client.Core.Constellation;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Core.Panes;

/// <summary>Registers the panes every client ships with.</summary>
public static class FirstPartyPanes
{
    // TODO(component): the kernel (Bazaar.Server.Components, IBazaarComponent) is server-side and the client has no DI
    // container yet. Decide how client components register (a head composition root or a client kernel), then turn this
    // into per-component registrations. Until then the heads call CreateRegistry.
    /// <param name="report">Called once per kind that falls back to the generic view.</param>
    /// <param name="chat">The group chat the GC pane shows. Null when no session is connected; the pane then shows its empty state.</param>
    public static PaneRegistry CreateRegistry(Action<string>? report = null, ChatViewModel? chat = null)
    {
        var registry = new PaneRegistry(report);
        registry.Register(PaneKinds.Kanban, descriptor => new KanbanPaneViewModel(descriptor));
        registry.Register(PaneKinds.Dashboard, descriptor => new DashboardPaneViewModel(descriptor));
        registry.Register(PaneKinds.Constellation, descriptor => new ConstellationPaneViewModel(descriptor));
        registry.Register(GcPaneViewModel.PaneKind, descriptor => new GcPaneViewModel(descriptor, chat));
        return registry;
    }
}
