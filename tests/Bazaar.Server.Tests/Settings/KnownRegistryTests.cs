using Bazaar.Server.Settings;

namespace Bazaar.Server.Tests.Settings;

public class KnownRegistryTests
{
    [Theory]
    [InlineData("constellation.node_cap")]
    [InlineData("constellation.ring_depth")]
    [InlineData("constellation.git_layout")]
    [InlineData("constellation.orbit_radius")]
    [InlineData("constellation.screenshot_sources")]
    public void Known_registry_holds_every_constellation_key_the_store_must_read(string name)
    {
        Assert.True(BuiltInSettingRegistry.Known.TryGet(name, out var definition));
        Assert.NotNull(definition);
    }

    [Fact]
    public void Known_registry_still_holds_the_host_keys()
    {
        foreach (var host in SettingRegistry.Host.Definitions)
        {
            Assert.True(BuiltInSettingRegistry.Known.Contains(host));
        }
    }

    [Fact]
    public void With_no_contributions_the_registry_matches_known()
    {
        var registry = BuiltInSettingRegistry.WithContributions([]);

        Assert.Equal(
            BuiltInSettingRegistry.Known.Definitions.Select(d => d.Name).Order(StringComparer.Ordinal),
            registry.Definitions.Select(d => d.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_module_contribution_is_aggregated_with_the_built_in_keys()
    {
        var key = new SettingKey<bool>("widgets.enabled", false, SettingScope.Install, SettingRole.Operator, "Test key.");
        var contribution = new FakeContribution([key]);

        var registry = BuiltInSettingRegistry.WithContributions([contribution]);

        Assert.True(registry.Contains(key));
        Assert.Equal(BuiltInSettingRegistry.Known.Definitions.Count + 1, registry.Definitions.Count);
        Assert.True(BuiltInSettingRegistry.Known.TryGet("host.status.port", out _));
        Assert.True(registry.TryGet("host.status.port", out _));
    }

    [Fact]
    public void A_contribution_that_repeats_a_built_in_key_is_rejected()
    {
        var clash = new SettingKey<int>("host.status.port", 1, SettingScope.Install, SettingRole.Operator, "Clash.");

        Assert.Throws<ArgumentException>(() => BuiltInSettingRegistry.WithContributions([new FakeContribution([clash])]));
    }

    [Fact]
    public void Two_contributions_that_repeat_a_name_are_rejected()
    {
        var first = new SettingKey<bool>("widgets.enabled", false, SettingScope.Install, SettingRole.Operator, "First.");
        var second = new SettingKey<bool>("widgets.enabled", true, SettingScope.Install, SettingRole.Operator, "Second.");

        Assert.Throws<ArgumentException>(() => BuiltInSettingRegistry.WithContributions([new FakeContribution([first]), new FakeContribution([second])]));
    }

    [Fact]
    public void Null_contributions_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => BuiltInSettingRegistry.WithContributions(null!));
    }

    private sealed class FakeContribution(IReadOnlyList<SettingDefinition> settings) : ISettingContribution
    {
        public IReadOnlyList<SettingDefinition> Settings { get; } = settings;
    }
}
