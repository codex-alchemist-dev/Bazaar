using Bazaar.Contracts.Gateway;
using Bazaar.Core;
using Bazaar.Server.Gateway.Usage;
using Bazaar.Server.Host.Composition;
using Bazaar.Server.Observatory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Host.Tests;

public class ObservatoryBridgeTests
{
    private static readonly DateTimeOffset _at = new(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);

    private static GatewayUsageRecord Row(string model) => new(
        Guid.NewGuid(), _at, "a1", "t1", "anthropic-direct", "k", model, null, GatewayIdentity.Native,
        new GatewayTokenUsage(1_000_000, 0, 0, 0, 1_000_000, 0), 5, UsageStatus.Success, null, null, false, null);

    [Fact]
    public void Prices_a_row_from_the_gateway_price_table_and_leaves_unknown_models_unpriced()
    {
        var table = new PriceTable("2026-11", [new PriceEntry("anthropic-direct", "haiku", new DateOnly(2026, 1, 1), 1m, 5m)]);
        var calculator = new PriceTableCostCalculator(table);

        Assert.Equal(6m, calculator.Price(Row("haiku")));
        Assert.Null(calculator.Price(Row("unlisted")));
    }

    [Fact]
    public void With_no_price_table_every_row_is_unpriced() =>
        Assert.Null(new PriceTableCostCalculator(null).Price(Row("haiku")));

    [Fact]
    public void Host_lists_the_observatory_components_and_the_graph_orders()
    {
        var ids = HostComponents.All.Select(c => c.Manifest.Id).ToArray();

        Assert.Contains("observatory", ids);
        Assert.Contains("observatory-postgres", ids);
        Assert.Contains("observatory-bridge", ids);
        Assert.NotNull(ComponentGraph.Order(HostComponents.All.Select(c => c.Manifest)));
    }

    [Fact]
    public void Bridge_registers_the_calculator_without_a_price_table()
    {
        var services = new ServiceCollection();
        new ObservatoryBridgeComponent().AddServices(services, new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        Assert.IsType<PriceTableCostCalculator>(provider.GetRequiredService<IUsageCostCalculator>());
    }
}
