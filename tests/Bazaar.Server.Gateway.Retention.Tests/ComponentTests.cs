using Bazaar.Server.Retention;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Gateway.Retention.Tests;

public sealed class ComponentTests
{
    [Fact]
    public void The_component_depends_on_retention_and_provides_the_gateway_usage_store()
    {
        var manifest = GatewayRetentionComponent.ManifestDefinition;
        Assert.Equal("gateway-retention", manifest.Id);
        Assert.Contains("retention", manifest.DependsOn);
    }

    [Fact]
    public void The_inventory_lists_gw_usage_as_holding_no_personal_data_and_needs_no_retention_row()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();
        new RetentionComponent().AddServices(services, config);
        new GatewayRetentionComponent().AddServices(services, config);
        services.AddSingleton(Npgsql.NpgsqlDataSource.Create("Host=localhost;Database=none"));
        using var provider = services.BuildServiceProvider();

        var entry = Assert.Single(provider.GetRequiredService<IDataInventory>().Entries);

        Assert.Equal("gateway_usage", entry.Category);
        Assert.Equal("gw_usage", entry.Store);
        Assert.False(entry.ContainsPersonalData);
        Assert.IsType<GatewayUsageRetentionStore>(Assert.Single(provider.GetServices<IRetentionStore>()));
    }

    [Fact]
    public async Task Store_ids_that_are_not_guids_are_refused_before_any_query()
    {
        using var source = Npgsql.NpgsqlDataSource.Create("Host=localhost;Database=none");
        var store = new GatewayUsageRetentionStore(source);

        await Assert.ThrowsAsync<ArgumentException>(() => store.PurgeAsync(["not-a-guid"], TestContext.Current.CancellationToken));
    }
}
