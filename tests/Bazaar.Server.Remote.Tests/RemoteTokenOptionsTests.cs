using Bazaar.Server.Components.Remote;

namespace Bazaar.Server.Remote.Tests;

public sealed class RemoteTokenOptionsTests
{
    [Theory]
    [InlineData(int.MaxValue, 30, 90)]
    [InlineData(15, int.MaxValue, int.MaxValue)]
    [InlineData(15, 30, int.MaxValue)]
    public void A_lifetime_the_clock_cannot_represent_is_refused_at_startup_not_on_every_refresh(int accessMinutes, int slidingDays, int absoluteDays)
    {
        var options = new RemoteTokenOptions
        {
            AccessTokenMinutes = accessMinutes,
            RefreshSlidingDays = slidingDays,
            RefreshAbsoluteDays = absoluteDays,
        };

        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }

    [Fact]
    public void The_shipped_defaults_are_valid()
    {
        new RemoteTokenOptions().Validate();
    }
}
