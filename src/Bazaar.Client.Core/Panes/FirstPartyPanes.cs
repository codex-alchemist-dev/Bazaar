using Bazaar.Client.Core.Constellation;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Core.Panes;

/// <summary>Registers the panes every client ships with.</summary>
public static class FirstPartyPanes
{
    // TODO(component): the kernel (Bazaar.Server.Components, IBazaarComponent) is server-side and the client has no DI
    // container yet. Decide how client components register (a head composition root or a client kernel), then turn this
    // into per-component registrations. Until then the heads call CreateRegistry.
    public static PaneRegistry CreateRegistry(Action<string>? report = null)
    {
        var registry = new PaneRegistry(report);
        registry.Register(PaneKinds.Kanban, descriptor => new KanbanPaneViewModel(descriptor));
        registry.Register(PaneKinds.Dashboard, descriptor => new DashboardPaneViewModel(descriptor));
        registry.Register(PaneKinds.Constellation, descriptor => new ConstellationPaneViewModel(descriptor));
        return registry;
    }
}
