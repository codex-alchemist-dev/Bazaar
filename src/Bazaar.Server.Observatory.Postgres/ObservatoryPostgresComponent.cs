using Cdax.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Observatory.Postgres;

/// <summary>
/// Registers the Postgres usage and lane readers for the Observatory (doc 23). Needs an <c>NpgsqlDataSource</c> from the host.
/// The price calculator is supplied by the host, because the price table lives with the gateway.
/// </summary>
public sealed class ObservatoryPostgresComponent : ICdaxModule
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "observatory-postgres",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: ["observatory"],
        Provides: ["observatory:usage-reader", "observatory:lane-reader"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IUsageReader, PostgresUsageReader>();
        services.AddSingleton<ILaneReader, PostgresLaneReader>();
    }
}
