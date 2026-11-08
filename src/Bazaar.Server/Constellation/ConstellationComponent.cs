using Bazaar.Server.Components;
using Bazaar.Server.Constellation.Tiles;
using Bazaar.Server.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Constellation;

/// <summary>
/// Constellation as a Bazaar Component (docs/design/19-constellation-and-git-solar-system.md, docs/design/BAZAAR-COMPONENTS.md).
/// It registers the tile handler, the on-demand layout tile source and the default (empty) graph source. The layout and clustering code is pure library code and needs no
/// registration. It depends on no other component.
/// </summary>
/// <remarks>
/// Migration 0020 (<c>constellation_views</c>) stays in <c>Data/Sql/common/</c> by decision (coordinator, 2026-11-01). Moving an
/// applied migration changes its identity, so the component's migration namespace is not used yet. A later wave moves it.
/// </remarks>
public sealed class ConstellationComponent : IBazaarComponent
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "constellation",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: [],
        Provides: ["feature:constellation", "feature:constellation:tiles"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton<IConstellationGraphSource, ConstellationEmptyGraphSource>();
        services.TryAddSingleton<IConstellationTileSource, LayoutTileSource>();

        // TODO(settings): read the stored constellation.node_cap per request. Until the settings reader is wired here, the handler
        // uses the shipped default, which is the same bound the settings store enforces.
        services.TryAddSingleton(sp => new ConstellationTileHandler(
            sp.GetRequiredService<IConstellationTileSource>(),
            ConstellationSettings.NodeCap.Default));
    }
}
