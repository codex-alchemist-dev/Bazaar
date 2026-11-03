using Bazaar.Contracts.Soul;
using Bazaar.Server.Gateway.Policy;
using Bazaar.Server.Souls.Presets;

namespace Bazaar.Server.Tests.Souls.Presets;

public sealed class DefaultPresetRosterTests
{
    [Fact]
    public void The_shipped_roster_loads_and_validates()
    {
        var roster = DefaultPresetRoster.Load();

        Assert.Equal(["claude-architect", "charles-boilerplate", "junior-specialist"], roster.Select(p => p.Id));
    }

    [Fact]
    public void Charles_and_the_juniors_route_through_custom_routes_over_a_cheap_whitelist()
    {
        var roster = DefaultPresetRoster.Load().ToDictionary(p => p.Id, StringComparer.Ordinal);

        foreach (var id in new[] { "charles-boilerplate", "junior-specialist" })
        {
            var policy = roster[id].ModelPolicy;
            Assert.StartsWith(RouteNames.DelegatedPrefix, policy.Route, StringComparison.Ordinal);
            Assert.Contains("anthropic-direct:claude-haiku-5-5", policy.Whitelist);
        }
    }

    [Fact]
    public void Every_shipped_route_is_custom_or_listed_in_its_whitelist()
    {
        foreach (var preset in DefaultPresetRoster.Load())
        {
            var policy = preset.ModelPolicy;
            var routeIsDelegated = RouteNames.IsDelegated(policy.Route);
            var routeIsListed = policy.Whitelist.Contains(policy.Route, StringComparer.Ordinal);

            Assert.True(routeIsDelegated || routeIsListed, $"{preset.Id}: route {policy.Route} is neither custom/* nor whitelisted.");
        }
    }

    [Fact]
    public void Every_shipped_whitelist_entry_is_a_concrete_provider_qualified_model()
    {
        foreach (var preset in DefaultPresetRoster.Load())
        {
            foreach (var entry in preset.ModelPolicy.Whitelist)
            {
                Assert.True(ModelRef.TryParse(entry, out _), $"{preset.Id}: whitelist entry {entry} is not provider-qualified.");
                Assert.False(RouteNames.IsAuto(entry), $"{preset.Id}: whitelist entry {entry} is a global auto route.");
            }
        }
    }

    [Fact]
    public void Shipped_presets_carry_the_current_schema_version_and_canonical_preferences()
    {
        foreach (var preset in DefaultPresetRoster.Load())
        {
            Assert.Equal(PresetSchema.Version, preset.SchemaVersion);
            Assert.StartsWith("{", preset.PreferencesJson, StringComparison.Ordinal);
        }
    }
}
