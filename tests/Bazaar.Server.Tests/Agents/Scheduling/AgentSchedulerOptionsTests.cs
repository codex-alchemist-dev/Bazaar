using Bazaar.Server.Agents.Scheduling;

namespace Bazaar.Server.Tests.Agents.Scheduling;

public sealed class AgentSchedulerOptionsTests
{
    [Fact]
    public void The_defaults_are_valid_and_the_loop_is_off()
    {
        var options = new AgentSchedulerOptions();

        options.Validate();

        Assert.False(options.Enabled);
        Assert.Equal(TimeSpan.FromSeconds(2), options.PollInterval);
        Assert.Equal(20, options.MaxAgentsPerPass);
        Assert.Equal(4, options.MaxConcurrentTurns);
        Assert.Equal(10, options.MaxItemsPerTurn);
        Assert.Equal(TimeSpan.FromMinutes(5), options.GuardTripBackoff);
        Assert.Equal("", options.Route);
        Assert.Equal("wake", options.HolderPrefix);
        Assert.Empty(options.ToolAllowlist);
    }

    [Fact]
    public void Enabled_with_a_route_is_valid()
    {
        new AgentSchedulerOptions { Enabled = true, Route = "claude", ToolAllowlist = ["gc.post"] }.Validate();
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Enabled_with_a_blank_route_is_refused(string route)
    {
        var ex = Assert.Throws<ArgumentException>(() => new AgentSchedulerOptions { Enabled = true, Route = route }.Validate());

        Assert.Contains("Bazaar:AgentTurns:Scheduler:Route", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_blank_route_is_fine_while_disabled()
    {
        new AgentSchedulerOptions { Enabled = false, Route = "" }.Validate();
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
    public void A_blank_holder_prefix_or_tool_name_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new AgentSchedulerOptions { HolderPrefix = " " }.Validate());
        Assert.Throws<ArgumentException>(() => new AgentSchedulerOptions { ToolAllowlist = ["gc.post", " "] }.Validate());
    }
}
