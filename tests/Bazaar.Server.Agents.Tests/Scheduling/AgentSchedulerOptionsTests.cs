using Bazaar.Server.Agents.Scheduling;

namespace Bazaar.Server.Tests.Agents.Scheduling;

public sealed class AgentSchedulerOptionsTests
{
    [Fact]
    public void The_defaults_are_valid_and_the_loop_is_off()
    {
        var options = new AgentSchedulerOptions();

        options.Validate();

        Assert.True(options.Enabled);
        Assert.Equal(TimeSpan.FromSeconds(2), options.PollInterval);
        Assert.Equal(20, options.MaxAgentsPerPass);
        Assert.Equal(4, options.MaxConcurrentTurns);
        Assert.Equal(10, options.MaxItemsPerTurn);
        Assert.Equal(TimeSpan.FromMinutes(5), options.GuardTripBackoff);
        Assert.Equal("wake", options.HolderPrefix);
    }

    [Fact]
    public void Enabled_needs_no_route_or_tool_names_because_both_are_per_agent()
    {
        new AgentSchedulerOptions { Enabled = true }.Validate();
    }

    [Fact]
    public void A_poll_interval_below_100_ms_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new AgentSchedulerOptions { PollInterval = TimeSpan.FromMilliseconds(99) }.Validate());
        new AgentSchedulerOptions { PollInterval = TimeSpan.FromMilliseconds(100) }.Validate();
    }

    [Fact]
    public void Limits_below_one_are_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentSchedulerOptions { MaxAgentsPerPass = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentSchedulerOptions { MaxConcurrentTurns = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentSchedulerOptions { MaxItemsPerTurn = 0 }.Validate());
    }

    [Fact]
    public void A_negative_guard_backoff_is_refused_and_zero_is_allowed()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new AgentSchedulerOptions { GuardTripBackoff = TimeSpan.FromSeconds(-1) }.Validate());
        new AgentSchedulerOptions { GuardTripBackoff = TimeSpan.Zero }.Validate();
    }

    [Fact]
    public void A_blank_holder_prefix_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new AgentSchedulerOptions { HolderPrefix = " " }.Validate());
    }
}
