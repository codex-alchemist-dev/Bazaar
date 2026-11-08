using Bazaar.Server.Components;
using Bazaar.Server.Kanban;
using Bazaar.Server.Kanban.Data;
using Bazaar.Server.Vault;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Bazaar.Server.Tests.Vault;

public class VaultComponentTests
{
    private sealed class MarkerComponent(string id) : IBazaarComponent
    {
        public ComponentManifest Manifest { get; } = new(id, "0.1.0", ComponentKind.Feature, [], []);

        public void AddServices(IServiceCollection services, IConfiguration configuration) =>
            services.AddSingleton(new Marker(id));
    }

    private sealed record Marker(string Id);

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    [Fact]
    public void Vault_manifest_is_a_feature_with_no_dependencies_and_provides_the_vault_feature()
    {
        var manifest = new VaultComponent().Manifest;

        Assert.Equal("vault", manifest.Id);
        Assert.Equal("0.1.0", manifest.Version);
        Assert.Equal(ComponentKind.Feature, manifest.Kind);
        Assert.Empty(manifest.DependsOn);
        Assert.Equal(["feature:vault"], manifest.Provides);
    }

    [Fact]
    public void Vault_manifest_id_and_provides_are_unique_among_the_host_components()
    {
        var ordered = ComponentGraph.Order([new KanbanComponent().Manifest, new VaultComponent().Manifest]);

        Assert.Equal(2, ordered.Select(m => m.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(2, ordered.SelectMany(m => m.Provides).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Host_like_collection_with_vault_resolves_without_a_database_connection()
    {
        // NpgsqlDataSource.Create does not connect, so the graph can be built with no database running.
        using var dataSource = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=bazaar");
        var services = new ServiceCollection();
        services.AddSingleton(dataSource);

        services.AddBazaarComponents(Config(), [new KanbanComponent(), new VaultComponent()]);

        using var provider = services.BuildServiceProvider();
        Assert.Same(dataSource, provider.GetRequiredService<NpgsqlDataSource>());
        Assert.NotNull(provider.GetRequiredService<TaskService>());
    }

    [Fact]
    public void Enabled_vault_slot_is_invoked_by_the_kernel()
    {
        var services = new ServiceCollection();

        services.AddBazaarComponents(Config(), [new MarkerComponent("vault")]);

        Assert.Single(services, d => d.ServiceType == typeof(Marker));
    }

    [Fact]
    public void Disabled_vault_component_is_not_registered()
    {
        var services = new ServiceCollection();

        services.AddBazaarComponents(Config(("Bazaar:Components:vault:Enabled", "false")), [new MarkerComponent("vault")]);

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(Marker));
    }

    [Fact]
    public void Vault_component_registers_the_read_side_search_services()
    {
        var services = new ServiceCollection();

        new VaultComponent().AddServices(services, Config());

        Assert.Contains(services, d => d.ServiceType == typeof(Bazaar.Server.Vault.Search.VaultSearchService));
        Assert.Contains(services, d => d.ServiceType == typeof(Bazaar.Server.Vault.Search.VaultSearchOptions));
    }
}
