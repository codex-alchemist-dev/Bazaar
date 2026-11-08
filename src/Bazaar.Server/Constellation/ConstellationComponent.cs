using Bazaar.Server.Components;
using Bazaar.Server.Constellation.Tiles;
using Bazaar.Server.Settings;
using Bazaar.Server.Vault.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

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

    /// <summary>Configuration key for the Vault whose documents form the graph. Without it the graph stays empty.</summary>
    public const string VaultIdKey = "Constellation:VaultId";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // The Vault graph wins when a Vault is configured. It is added before the empty default, so TryAdd keeps it.
        if (Guid.TryParse(configuration[VaultIdKey], out var vaultId))
        {
            services.TryAddSingleton<IConstellationGraphSource>(sp => new VaultGraphSource(
                new VaultIndexRepository(sp.GetRequiredService<NpgsqlDataSource>()),
                vaultId));
        }

        services.TryAddSingleton<IConstellationGraphSource, ConstellationEmptyGraphSource>();
        services.TryAddSingleton<IConstellationTileSource, LayoutTileSource>();

        // TODO(settings): read the stored constellation.node_cap per request. Until the settings reader is wired here, the handler
        // uses the shipped default, which is the same bound the settings store enforces.
        services.TryAddSingleton(sp => new ConstellationTileHandler(
            sp.GetRequiredService<IConstellationTileSource>(),
            ConstellationSettings.NodeCap.Default));
    }
}
