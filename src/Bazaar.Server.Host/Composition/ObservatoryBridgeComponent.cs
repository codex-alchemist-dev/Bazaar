using Bazaar.Contracts.Gateway;
using Cdax.Core;
using Bazaar.Server.Gateway.Usage;
using Bazaar.Server.Observatory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Host.Composition;

/// <summary>
/// Prices Observatory usage rows from the gateway's dated price table (doc 23, Usage accounting). The host is the only place that
/// sees both modules, so the bridge lives here. With no <see cref="PriceTable"/> registered every row is unpriced, and the
/// Observatory then shows cost as unknown, never zero.
/// </summary>
public sealed class PriceTableCostCalculator(PriceTable? prices) : IUsageCostCalculator
{
    public decimal? Price(GatewayUsageRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var entry = prices?.Lookup(record.ProviderId, record.ModelId, record.Timestamp);
        return UsageCostCalculator.Compute(record.Tokens, entry);
    }
}

/// <summary>Registers <see cref="PriceTableCostCalculator"/> for the Observatory. Takes the gateway's <see cref="PriceTable"/> when one is registered.</summary>
public sealed class ObservatoryBridgeComponent : ICdaxModule
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "observatory-bridge",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: ["observatory"],
        Provides: ["observatory:price-calculator"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration) =>
        services.TryAddSingleton<IUsageCostCalculator>(sp => new PriceTableCostCalculator(sp.GetService<PriceTable>()));
}
