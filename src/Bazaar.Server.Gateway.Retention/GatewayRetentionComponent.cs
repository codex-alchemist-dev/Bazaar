using Cdax.Core;
using Bazaar.Server.Retention;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Gateway.Retention;

/// <summary>
/// Gateway usage retention (doc 05, doc 48 section 9): registers the <c>gw_usage</c> store with the retention sweeper and its data
/// inventory entry. The period is the retention table row <c>Bazaar:Retention:gateway_usage</c> (default keep). Needs an
/// <c>NpgsqlDataSource</c> from the host.
/// </summary>
public sealed class GatewayRetentionComponent : ICdaxModule
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "gateway-retention",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: ["retention"],
        Provides: ["retention:store:gateway_usage"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IRetentionStore, GatewayUsageRetentionStore>();
        services.AddSingleton<IDataInventoryContributor, GatewayInventory>();
    }

    private sealed class GatewayInventory : IDataInventoryContributor
    {
        // Usage rows hold agent ids, token counts and model ids; no prompt or completion text and no person (doc 05).
        public IReadOnlyList<DataInventoryEntry> Entries { get; } =
        [
            new DataInventoryEntry(GatewayUsageRetentionStore.CategoryId, "gw_usage", [], "Model call accounting per agent (tokens, latency, status)", ContainsPersonalData: false),
        ];
    }
}
