using System.Text.Json;
using Bazaar.Client.Core.Panes;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Constellation.Tests;

/// <summary>Pane fixtures for the Constellation tests: the Constellation module alone, with an optional tile source.</summary>
internal static class ConstellationTestPanes
{
    public static PaneRegistry CreateRegistry(IConstellationTileSource? tiles = null) =>
        PaneRegistry.Compose(
            [new ConstellationPaneModule()],
            tiles is null ? null : new PaneModuleContext().With<IConstellationTileSource>(tiles));

    internal static PaneDescriptor Descriptor(string kind, string json = "{}") =>
        new(kind, "Title", string.Empty, JsonElement.Parse(json));
}
