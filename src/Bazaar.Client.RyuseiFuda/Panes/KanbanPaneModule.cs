using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Contracts.Modules;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.RyuseiFuda.Panes;

/// <summary>Registers the Kanban dependency board (<see cref="PaneKinds.Kanban"/>).</summary>
public sealed class KanbanPaneModule : IPaneModule, IClientModule
{
    /// <summary>The manifest the host checks at load (doc 41 section 8).</summary>
    public ModuleManifest Manifest { get; } = FirstPartyManifest.Create(typeof(KanbanPaneModule), PaneKinds.Kanban, [PaneKinds.Kanban], ["ui:pane", "wire:kanban.read"]);

    /// <summary>Records the pane kinds this module registers, as its manifest declares them.</summary>
    public void Register(ClientModuleBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        foreach (var kind in builder.Manifest.PaneKinds)
        {
            builder.AddPaneKind(kind);
        }
    }

    public string Id => PaneKinds.Kanban;

    public void Register(PaneRegistry registry, PaneModuleContext context)
    {
        registry.Register(PaneKinds.Kanban, descriptor => new KanbanPaneViewModel(descriptor));
        registry.RegisterView<KanbanPaneViewModel>(RyuseiFudaPaneViews.KanbanBoard);
    }
}
