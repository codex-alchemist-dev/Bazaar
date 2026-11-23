using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Health;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Health.Tests;

public class HealthPanesTests
{
    [Fact]
    public void The_module_registers_both_panes_under_their_kinds()
    {
        var registry = PaneTestSupport.CreateRegistry();

        Assert.True(registry.Contains(PaneKinds.Health));
        Assert.True(registry.Contains(PaneKinds.Analytics));
        Assert.Equal(PaneKinds.Health, HealthPanes.Modules[0].Id);
        Assert.Equal(PaneKinds.Analytics, HealthPanes.Modules[1].Id);
    }
}
