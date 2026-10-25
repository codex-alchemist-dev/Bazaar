using Bazaar.Server.Agents.LoopGuard;
using Bazaar.Testing;

namespace Bazaar.Server.Tests.Agents.LoopGuard;

public class LoopGuardOptionsTests
{
    [Fact]
    public void Defaults_match_the_doc_06_table_and_doc_13_cap()
    {
        var options = new LoopGuardOptions();

        Assert.Equal(6, options.PingPongLimit);
        Assert.Equal(4, options.MentionDepth);
        Assert.Equal(30, options.WakesPerHour);
        Assert.Equal(20, options.ThreadAgentMessages);
        Assert.Equal(3, options.RepeatToolCall);
        Assert.Equal(20, options.ScoredWakesPerHour);
    }

    [Fact]
    public void Scored_cap_equal_to_the_wake_total_is_allowed()
    {
        var options = new LoopGuardOptions { ScoredWakesPerHour = 30, WakesPerHour = 30 };

        options.Validate();
    }

    [Fact]
    public void Scored_cap_above_the_wake_total_is_refused_at_construction()
    {
        var options = new LoopGuardOptions { ScoredWakesPerHour = 31, WakesPerHour = 30 };

        var ex = Assert.Throws<ArgumentException>(() => new LoopGuardFacade(new FakeClock(), options));
        Assert.Equal("ScoredWakesPerHour", ex.ParamName);
    }

    [Fact]
    public void A_zero_limit_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LoopGuardOptions { RepeatToolCall = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new LoopGuardOptions { PingPongLimit = 0 }.Validate());
    }

    [Fact]
    public void Null_options_use_the_defaults()
    {
        var guard = new LoopGuardFacade(new FakeClock());

        Assert.True(guard.ScoredWakeWithinCap("agent-a"));
    }
}
