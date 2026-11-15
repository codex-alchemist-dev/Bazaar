using Bazaar.Server.Agents.Turns;
using Bazaar.Server.Agents.Turns.Posting;
using Bazaar.Server.Components;
using Bazaar.Server.Gc;
using Bazaar.Server.Gc.Read;
using Bazaar.Server.Gc.Write;
using Bazaar.Server.Kanban;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Bazaar.Server.Tests.Gc;

public class GcComponentTests
{
    [Fact]
    public void Gc_manifest_is_a_feature_with_no_dependencies_and_the_two_gc_contracts()
    {
        var manifest = new GcComponent().Manifest;

        Assert.Equal("gc", manifest.Id);
        Assert.Equal(ComponentKind.Feature, manifest.Kind);
        Assert.Empty(manifest.DependsOn);
        Assert.Equal(["feature:gc-log", "feature:gc-wire"], manifest.Provides);
    }

    [Fact]
    public void Gc_and_kanban_manifests_have_unique_ids_and_provides()
    {
        var manifests = new IBazaarComponent[] { new GcComponent(), new KanbanComponent() }.Select(c => c.Manifest).ToList();

        Assert.Equal(manifests.Count, manifests.Select(m => m.Id).Distinct(StringComparer.Ordinal).Count());
        var provides = manifests.SelectMany(m => m.Provides).ToList();
        Assert.Equal(provides.Count, provides.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Gc_component_resolves_the_log_reader_writer_and_viewer_rules_without_a_database()
    {
        // NpgsqlDataSource.Create does not connect, so no database is needed to resolve the service graph.
        using var dataSource = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=bazaar");
        var services = new ServiceCollection();
        services.AddSingleton(dataSource);

        services.AddBazaarComponents(new ConfigurationBuilder().Build(), [new GcComponent()]);

        await using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<IGcLogWriter>());
        Assert.NotNull(provider.GetRequiredService<IGcLogReader>());
        Assert.NotNull(provider.GetRequiredService<IGcReadAccess>());
    }

    [Fact]
    public void Gc_component_registers_the_gc_agent_post_sink()
    {
        // The sink needs ILoopGuard, which the host registers, so this checks the registration rather than resolving it.
        using var dataSource = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=bazaar");
        var services = new ServiceCollection();
        services.AddSingleton(dataSource);

        services.AddBazaarComponents(new ConfigurationBuilder().Build(), [new GcComponent()]);

        Assert.Contains(services, d => d.ServiceType == typeof(IAgentPostSink)
            && d.ImplementationType == typeof(GcAgentPostSink));
    }

    [Fact]
    public void Disabled_gc_component_is_not_registered()
    {
        using var dataSource = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=bazaar");
        var services = new ServiceCollection();
        services.AddSingleton(dataSource);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Bazaar:Components:gc:Enabled"] = "false" })
            .Build();

        services.AddBazaarComponents(configuration, [new GcComponent()]);

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IGcLogWriter));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IGcLogReader));
    }
}
