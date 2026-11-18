using Bazaar.Server.Interrupts;

namespace Bazaar.Server.Tests.Interrupts;

public class InterruptWakePolicyTests
{
    [Fact]
    public void An_owner_push_tagged_emergency_bypasses_vacation()
    {
        Assert.True(InterruptWakePolicy.BypassesVacation(InterruptSource.Owner, emergencyTag: true));
    }

    [Fact]
    public void An_untagged_owner_push_does_not_bypass_vacation()
    {
        Assert.False(InterruptWakePolicy.BypassesVacation(InterruptSource.Owner, emergencyTag: false));
    }

    [Theory]
    [InlineData(InterruptSource.SystemBuild)]
    [InlineData(InterruptSource.SystemSecurity)]
    public void A_system_push_never_bypasses_vacation_even_when_tagged(InterruptSource source)
    {
        Assert.False(InterruptWakePolicy.BypassesVacation(source, emergencyTag: true));
    }
}
