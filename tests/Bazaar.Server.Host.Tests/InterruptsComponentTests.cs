using Cdax.Core;
using Bazaar.Server.Host.Composition;
using Bazaar.Server.Interrupts;

namespace Bazaar.Server.Host.Tests;

public sealed class InterruptsComponentTests
{
    [Fact]
    public void Host_ships_the_interrupts_component_after_gc_and_setup()
    {
        Assert.Contains(HostComponents.All, c => c.Manifest.Id == "interrupts");

        var order = ComponentGraph.Order(HostComponents.All.Select(c => c.Manifest)).Select(m => m.Id).ToList();
        var interrupts = order.IndexOf("interrupts");
        Assert.True(order.IndexOf("gc") < interrupts);
        Assert.True(order.IndexOf("setup") < interrupts);
        Assert.Equal(["gc", "setup"], InterruptsComponent.ManifestDefinition.DependsOn);
    }
}
