using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Core.Panes;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.RyuseiFuda.Panes;

/// <summary>Registers the Kanban dependency board (<see cref="PaneKinds.Kanban"/>).</summary>
public sealed class KanbanPaneModule : IPaneModule
{
    public string Id => PaneKinds.Kanban;

    public void Register(PaneRegistry registry, PaneModuleContext context)
    {
        registry.Register(PaneKinds.Kanban, descriptor => new KanbanPaneViewModel(descriptor));
        registry.RegisterView<KanbanPaneViewModel>(RyuseiFudaPaneViews.KanbanBoard);
    }
}
