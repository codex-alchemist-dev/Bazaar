using Bazaar.Server.Host.Composition;

namespace Bazaar.Server.Host.Tests;

/// <summary>The Garnet cache module is part of the host's component list (doc 03 WP12, spike S2 passed).</summary>
public sealed class GarnetHostWiringTests
{
    [Fact]
    public void The_host_component_list_contains_garnet_next_to_valkey()
    {
        var ids = HostComponents.All.Select(c => c.Manifest.Id).ToList();

        Assert.Contains("garnet", ids);
        Assert.Contains("valkey", ids);
    }
}
