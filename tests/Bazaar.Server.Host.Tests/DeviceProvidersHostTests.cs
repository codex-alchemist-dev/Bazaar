using Bazaar.Core;
using Bazaar.Server.Host.Composition;
using Bazaar.Server.Vms.Devices;

namespace Bazaar.Server.Host.Tests;

/// <summary>The device providers component is part of the host (doc 62 section 9.1).</summary>
public sealed class DeviceProvidersHostTests
{
    [Fact]
    public void The_host_ships_the_vms_devices_component_after_settings()
    {
        Assert.Contains(HostComponents.All, c => c is DeviceProvidersComponent);

        var order = ComponentGraph.Order(HostComponents.All.Select(c => c.Manifest)).Select(m => m.Id).ToList();

        Assert.True(order.IndexOf("settings") < order.IndexOf("vms-devices"));
    }
}
