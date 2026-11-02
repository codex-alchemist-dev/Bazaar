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
        Assert.True(SettingRegistry.Known.TryGet(name, out var definition));
        Assert.NotNull(definition);
    }

    [Fact]
    public void Known_registry_still_holds_the_host_keys()
    {
        foreach (var host in SettingRegistry.Host.Definitions)
        {
            Assert.True(SettingRegistry.Known.Contains(host));
        }
    }
}
