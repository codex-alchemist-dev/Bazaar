using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Core.Panes;

/// <summary>Registers the RyuseiFuda objective roadmap pane (<see cref="PaneKinds.Roadmap"/>).</summary>
public sealed class RoadmapPaneModule : IPaneModule
{
    public string Id => PaneKinds.Roadmap;

    public void Register(PaneRegistry registry, PaneModuleContext context) =>
        registry.Register(PaneKinds.Roadmap, descriptor => new RoadmapPaneViewModel(descriptor));
}
