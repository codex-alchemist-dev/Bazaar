using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Contracts.Modules;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.RyuseiFuda.Panes;

/// <summary>Registers the RyuseiFuda objective roadmap pane (<see cref="PaneKinds.Roadmap"/>).</summary>
public sealed class RoadmapPaneModule : IPaneModule, IClientModule
{
    /// <summary>The manifest the host checks at load (doc 41 section 8).</summary>
    public ModuleManifest Manifest { get; } = FirstPartyManifest.Create(typeof(RoadmapPaneModule), PaneKinds.Roadmap, [PaneKinds.Roadmap], ["ui:pane", "wire:kanban.read"]);

    /// <summary>Records the pane kinds this module registers, as its manifest declares them.</summary>
    public void Register(ClientModuleBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        foreach (var kind in builder.Manifest.PaneKinds)
        {
            builder.AddPaneKind(kind);
        }
    }

    public string Id => PaneKinds.Roadmap;

    public void Register(PaneRegistry registry, PaneModuleContext context)
    {
        registry.Register(PaneKinds.Roadmap, descriptor => new RoadmapPaneViewModel(descriptor));
        registry.RegisterView<RoadmapPaneViewModel>(RyuseiFudaPaneViews.Roadmap);
    }
}
