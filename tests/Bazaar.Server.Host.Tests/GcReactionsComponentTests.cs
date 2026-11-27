using Cdax.Core;
using Bazaar.Server.Data.Migrations;
using Bazaar.Server.GcReactions;
using Bazaar.Server.Host.Composition;
using Bazaar.Server.Host.Startup;
using Bazaar.Server.Wire.Connection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Host.Tests;

public sealed class GcReactionsComponentTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    [Fact]
    public void Manifest_names_gc_reactions_as_a_feature_that_needs_the_gc_log()
    {
        var manifest = GcReactionsComponent.ManifestDefinition;

        Assert.Equal("gc-reactions", manifest.Id);
        Assert.Equal(ComponentKind.Feature, manifest.Kind);
        Assert.Equal(["gc"], manifest.DependsOn);
        Assert.Equal(GcReactionsComponent.MigrationProvider, manifest.MigrationNamespace);
    }

    [Fact]
    public void Host_ships_the_component_after_the_gc_log_it_depends_on()
    {
        Assert.Contains(HostComponents.All, c => c.Manifest.Id == "gc-reactions");

        var order = ComponentGraph.Order(HostComponents.All.Select(c => c.Manifest)).Select(m => m.Id).ToList();

        Assert.True(order.IndexOf("gc") < order.IndexOf("gc-reactions"));
    }

    [Fact]
    public void Host_migration_source_includes_the_gc_reactions_namespace()
    {
        var source = HostMigrations.Source();

        Assert.Contains("gc_reactions", source.Namespaces());
        Assert.Equal([1], source.Load("gc_reactions").Select(f => f.Version).ToList());
    }

    [Fact]
    public void The_component_registers_the_three_wire_handlers_and_binds_the_operator_limits()
    {
        var services = new ServiceCollection();

        new GcReactionsComponent().AddServices(services, Config(("Bazaar:GcReactions:RatePerMinute", "7")));

        var handlers = services.Where(d => d.ServiceType == typeof(IWireRequestHandler)).Select(d => d.ImplementationType).ToList();
        Assert.Equal([typeof(GcReactHandler), typeof(GcReactionsHandler), typeof(GcTypingHandler)], handlers);
        using var provider = services.BuildServiceProvider();
        Assert.Equal(7, provider.GetRequiredService<GcReactionsOptions>().RatePerMinute);
    }

    [Fact]
    public void A_bad_limit_fails_at_start()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new GcReactionsComponent().AddServices(new ServiceCollection(), Config(("Bazaar:GcReactions:TypingTtlS", "0"))));
    }
}
