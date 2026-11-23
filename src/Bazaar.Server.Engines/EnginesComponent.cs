using Bazaar.Core;
using Bazaar.Server.EngineDispatch;
using Bazaar.Server.Engines.Grants;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Bazaar.Server.Engines;

/// <summary>
/// The Engines module as a Bazaar Component (docs/design/20). Registers the Postgres grant store and the
/// <see cref="IEngineGrantReader"/> the dispatcher reads. Its SQL is the <c>engines</c> migration namespace, embedded in this
/// assembly under <c>Data/Sql/engines</c>. Needs the host's <see cref="NpgsqlDataSource"/>; the grant store and reader resolve only
/// when it is present, so a host without a database keeps the relay's deny-all default.
/// </summary>
public sealed class EnginesComponent : IBazaarComponent
{
    /// <summary>Migration namespace and history key for this component's SQL.</summary>
    public const string MigrationProvider = "engines";

    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "engines",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: [],
        Provides: ["feature:engines"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(sp => new EngineGrantStore(sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddSingleton<IEngineGrantReader>(sp => new PostgresEngineGrantReader(sp.GetRequiredService<EngineGrantStore>()));
    }
}
