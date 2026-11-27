using Cdax.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Tests.Components;

public class ComponentServiceCollectionTests
{
    private sealed class FakeComponent(string id, Action<IServiceCollection>? register = null, params string[] dependsOn) : ICdaxModule
    {
        public bool EnabledByDefault { get; init; } = true;

        public ComponentManifest Manifest => new(id, "0.1.0", ComponentKind.Feature, dependsOn, [], EnabledByDefault: EnabledByDefault);

        public void AddServices(IServiceCollection services, IConfiguration configuration) => register?.Invoke(services);
    }

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    [Fact]
    public void Enabled_components_register_in_dependency_order()
    {
        var log = new List<string>();
        var services = new ServiceCollection();

        services.AddCdaxModules(Config(), [
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

        services.AddCdaxModules(Config(("Bazaar:Components:kanban:Enabled", "false")), [
            new FakeComponent("kanban", _ => log.Add("kanban")),
        ]);

        Assert.Empty(log);
    }

    [Fact]
    public void Enabled_component_that_needs_a_disabled_one_fails_the_start()
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<ComponentGraphException>(() => services.AddCdaxModules(
            Config(("Bazaar:Components:gc:Enabled", "false")),
            [new FakeComponent("gc"), new FakeComponent("agents", null, "gc")]));

        Assert.Contains("'agents' needs disabled component(s): gc", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Disabled_component_with_a_bad_graph_still_fails_the_start()
    {
        var services = new ServiceCollection();

        Assert.Throws<ComponentGraphException>(() => services.AddCdaxModules(
            Config(("Bazaar:Components:kanban:Enabled", "false")),
            [new FakeComponent("kanban", null, "missing")]));
    }

    [Fact]
    public void Unset_or_unparseable_enablement_means_enabled()
    {
        Assert.True(CdaxModuleRegistration.IsEnabled(Config(), "kanban"));
        Assert.True(CdaxModuleRegistration.IsEnabled(Config(("Bazaar:Components:kanban:Enabled", "maybe")), "kanban"));
    }

    [Fact]
    public void The_cdax_key_wins_and_the_old_bazaar_key_still_works_as_a_fallback()
    {
        Assert.False(CdaxModuleRegistration.IsEnabled(Config(("Cdax:Modules:kanban:Enabled", "false")), "kanban"));
        Assert.False(CdaxModuleRegistration.IsEnabled(Config(("Bazaar:Components:kanban:Enabled", "false")), "kanban"));
        Assert.True(CdaxModuleRegistration.IsEnabled(
            Config(("Cdax:Modules:kanban:Enabled", "true"), ("Bazaar:Components:kanban:Enabled", "false")), "kanban"));
    }

    [Fact]
    public void An_optional_component_is_not_registered_until_switched_on()
    {
        var log = new List<string>();

        new ServiceCollection().AddCdaxModules(Config(), [new FakeComponent("playground", _ => log.Add("playground")) { EnabledByDefault = false }]);

        Assert.Empty(log);
    }

    [Fact]
    public void An_explicit_setting_switches_an_optional_component_on()
    {
        var log = new List<string>();

        new ServiceCollection().AddCdaxModules(
            Config(("Bazaar:Components:playground:Enabled", "true")),
            [new FakeComponent("playground", _ => log.Add("playground")) { EnabledByDefault = false }]);

        Assert.Equal(["playground"], log);
    }

    [Fact]
    public void An_explicit_false_still_switches_a_default_on_component_off()
    {
        var log = new List<string>();

        new ServiceCollection().AddCdaxModules(Config(("Bazaar:Components:kanban:Enabled", "false")), [new FakeComponent("kanban", _ => log.Add("kanban"))]);

        Assert.Empty(log);
    }

    [Fact]
    public void An_unparsable_setting_falls_back_to_the_manifest_default()
    {
        var log = new List<string>();

        new ServiceCollection().AddCdaxModules(
            Config(("Bazaar:Components:playground:Enabled", "maybe")),
            [new FakeComponent("playground", _ => log.Add("playground")) { EnabledByDefault = false }]);

        Assert.Empty(log);
    }

    [Fact]
    public void An_optional_component_that_is_on_needs_its_optional_dependency_on_too()
    {
        var on = Config(("Bazaar:Components:b:Enabled", "true"));

        var ex = Assert.Throws<ComponentGraphException>(() => new ServiceCollection().AddCdaxModules(on,
        [
            new FakeComponent("b", null, "a") { EnabledByDefault = false },
            new FakeComponent("a") { EnabledByDefault = false },
        ]));

        Assert.Contains("'b'", ex.Message);
    }
}
