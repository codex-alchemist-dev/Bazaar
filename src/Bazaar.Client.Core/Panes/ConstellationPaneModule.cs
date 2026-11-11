using Bazaar.Client.Core.Constellation;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Core.Panes;

/// <summary>Registers the Constellation graph view (<see cref="PaneKinds.Constellation"/>).</summary>
public sealed class ConstellationPaneModule : IPaneModule
{
    public string Id => PaneKinds.Constellation;

    public void Register(PaneRegistry registry, PaneModuleContext context) =>
        registry.Register(PaneKinds.Constellation, descriptor =>
            new ConstellationPaneViewModel(descriptor, context.Tiles, ConstellationPaneViewModel.DefaultNodeCap));
}
