using Bazaar.Server.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Tests.Components;

public class ComponentServiceCollectionTests
{
    private sealed class FakeComponent(string id, Action<IServiceCollection>? register = null, params string[] dependsOn) : IBazaarComponent
    {
        public ComponentManifest Manifest { get; } = new(id, "0.1.0", ComponentKind.Feature, dependsOn, []);

        public void AddServices(IServiceCollection services, IConfiguration configuration) => register?.Invoke(services);
    }

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    [Fact]
    public void Enabled_components_register_in_dependency_order()
    {
        var log = new List<string>();
        var services = new ServiceCollection();

        services.AddBazaarComponents(Config(), [
            new FakeComponent("agents", _ => log.Add("agents"), "gc"),
            new FakeComponent("gc", _ => log.Add("gc")),
        ]);

        Assert.Equal(["gc", "agents"], log);
    }

    [Fact]
    public void Disabled_component_is_not_registered()
    {
        var log = new List<string>();
        var services = new ServiceCollection();

        services.AddBazaarComponents(Config(("Bazaar:Components:kanban:Enabled", "false")), [
            new FakeComponent("kanban", _ => log.Add("kanban")),
        ]);

        Assert.Empty(log);
    }

    [Fact]
    public void Enabled_component_that_needs_a_disabled_one_fails_the_start()
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<ComponentGraphException>(() => services.AddBazaarComponents(
            Config(("Bazaar:Components:gc:Enabled", "false")),
            [new FakeComponent("gc"), new FakeComponent("agents", null, "gc")]));

        Assert.Contains("'agents' needs disabled component(s): gc", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Disabled_component_with_a_bad_graph_still_fails_the_start()
    {
        var services = new ServiceCollection();

        Assert.Throws<ComponentGraphException>(() => services.AddBazaarComponents(
            Config(("Bazaar:Components:kanban:Enabled", "false")),
            [new FakeComponent("kanban", null, "missing")]));
    }

    [Fact]
    public void Unset_or_unparseable_enablement_means_enabled()
    {
        Assert.True(BazaarComponentRegistration.IsEnabled(Config(), "kanban"));
        Assert.True(BazaarComponentRegistration.IsEnabled(Config(("Bazaar:Components:kanban:Enabled", "maybe")), "kanban"));
    }
}
