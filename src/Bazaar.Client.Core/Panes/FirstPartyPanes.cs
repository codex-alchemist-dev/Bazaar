using Bazaar.Client.Core.Constellation;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Core.Panes;

/// <summary>Registers the panes every client ships with.</summary>
public static class FirstPartyPanes
{
    // TODO(component): once the component kernel (#125) merges, replace this with the
    // AddBazaarKanbanPanes / AddBazaarDashboardPanes registrations called from host composition.
    public static PaneRegistry CreateRegistry(Action<string>? report = null)
    {
        var registry = new PaneRegistry(report);
        registry.Register(PaneKinds.Kanban, descriptor => new KanbanPaneViewModel(descriptor));
        registry.Register(PaneKinds.Dashboard, descriptor => new DashboardPaneViewModel(descriptor));
        registry.Register(PaneKinds.Constellation, descriptor => new ConstellationPaneViewModel(descriptor));
        return registry;
    }
}
