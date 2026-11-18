using System.Text.Json;
using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Constellation.Abstractions;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Constellation;

/// <summary>Registers the Constellation graph view (<see cref="PaneKinds.Constellation"/>).</summary>
public sealed class ConstellationPaneModule : IPaneModule
{
    /// <summary>The Constellation pane with an empty model: no graph loaded yet. The pane fetches its tiles itself.</summary>
    public static PaneDescriptor WindowPane { get; } = new(PaneKinds.Constellation, "Constellation", string.Empty, JsonElement.Parse("{}"));

    public string Id => PaneKinds.Constellation;

    public void Register(PaneRegistry registry, PaneModuleContext context)
    {
        var tiles = context.Get<IConstellationTileSource>();
        registry.Register(PaneKinds.Constellation, descriptor =>
            new ConstellationPaneViewModel(descriptor, tiles, ConstellationPaneViewModel.DefaultNodeCap));
        registry.RegisterView<ConstellationPaneViewModel>(pane => new ConstellationPaneView(pane));
    }
}
