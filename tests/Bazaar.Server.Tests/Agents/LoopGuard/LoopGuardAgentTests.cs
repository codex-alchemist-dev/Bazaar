using Bazaar.Server.Agents.LoopGuard;
using Bazaar.Testing;

namespace Bazaar.Server.Tests.Agents.LoopGuard;

public class LoopGuardAgentTests
{
    private const string _a = "agent-a";
    private const string _b = "agent-b";
    private static readonly Guid _gc = Guid.Parse("0196a6f0-0000-7000-8000-000000000301");

    private static GuardVerdict Wake(LoopGuardFacade guard, string agent, WakeKind kind = WakeKind.Standard) =>
        guard.Check(new AgentWake(agent, kind));

    [Fact]
    public void Wakes_per_hour_trip_on_the_wake_past_the_limit()
    {
        var guard = new LoopGuardFacade(new FakeClock());
        for (var i = 0; i < 30; i++)
        {
            Assert.False(Wake(guard, _a).Tripped);
        }

        var verdict = Wake(guard, _a);

        Assert.Equal(GuardKind.WakesPerHour, verdict.Kind);
        Assert.Equal([_a], verdict.AgentIds);
    }

    [Fact]
    public void Wakes_are_counted_per_agent()
    {
        var guard = new LoopGuardFacade(new FakeClock());
        for (var i = 0; i < 30; i++)
        {
            Wake(guard, _a);
        }

        Assert.False(Wake(guard, _b).Tripped);
    }

    [Fact]
    public void Wakes_expire_from_the_sliding_window_after_one_hour()
    {
        var clock = new FakeClock();
        var guard = new LoopGuardFacade(clock);
        for (var i = 0; i < 30; i++)
        {
            Wake(guard, _a);
        }

        clock.Advance(TimeSpan.FromHours(1) - TimeSpan.FromSeconds(1));
        Assert.Equal(GuardKind.WakesPerHour, Wake(guard, _a).Kind);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(Wake(guard, _a).Tripped);
    }

    [Fact]
    public void Scored_wakes_count_toward_the_hourly_total()
    {
        var guard = new LoopGuardFacade(new FakeClock());
        for (var i = 0; i < 10; i++)
        {
            Wake(guard, _a, WakeKind.SkimmerScored);
        }

        for (var i = 0; i < 20; i++)
        {
            Wake(guard, _a);
        }

        Assert.Equal(GuardKind.WakesPerHour, Wake(guard, _a).Kind);
    }

    [Fact]
    public void Scored_cap_allows_up_to_the_cap_then_refuses_without_tripping()
    {
        var guard = new LoopGuardFacade(new FakeClock());
        for (var i = 0; i < 20; i++)
        {
            Assert.True(guard.ScoredWakeWithinCap(_a));
            Assert.False(Wake(guard, _a, WakeKind.SkimmerScored).Tripped);
        }

        Assert.False(guard.ScoredWakeWithinCap(_a));
        Assert.True(guard.ScoredWakeWithinCap(_b));
    }

    [Fact]
    public void Standard_wakes_do_not_use_the_scored_cap()
    {
        var guard = new LoopGuardFacade(new FakeClock());
        for (var i = 0; i < 25; i++)
        {
            Wake(guard, _a);
        }

        Assert.True(guard.ScoredWakeWithinCap(_a));
    }

    [Fact]
    public void Scored_cap_frees_up_when_scored_wakes_expire()
    {
        var clock = new FakeClock();
        var guard = new LoopGuardFacade(clock);
        for (var i = 0; i < 20; i++)
        {
            Wake(guard, _a, WakeKind.SkimmerScored);
        }

        Assert.False(guard.ScoredWakeWithinCap(_a));

        clock.Advance(TimeSpan.FromHours(1));

        Assert.True(guard.ScoredWakeWithinCap(_a));
    }

    [Fact]
    public void Repeat_tool_call_at_the_limit_does_not_trip()
    {
        var guard = new LoopGuardFacade(new FakeClock());
        var fingerprint = ToolCallFingerprint.Of("search", "{\"q\":\"x\"}");

        for (var i = 0; i < 3; i++)
        {
            Assert.False(guard.Check(new ToolCall(_a, fingerprint)).Tripped);
        }
    }

    [Fact]
    public void Repeat_tool_call_past_the_limit_trips()
    {
        var guard = new LoopGuardFacade(new FakeClock());
        var fingerprint = ToolCallFingerprint.Of("search", "{\"q\":\"x\"}");
        for (var i = 0; i < 3; i++)
        {
            guard.Check(new ToolCall(_a, fingerprint));
        }

        var verdict = guard.Check(new ToolCall(_a, fingerprint));

        Assert.Equal(GuardKind.RepeatToolCall, verdict.Kind);
        Assert.Equal([_a], verdict.AgentIds);
    }

    [Fact]
    public void A_different_tool_call_resets_the_repeat_run()
    {
        var guard = new LoopGuardFacade(new FakeClock());
        var first = ToolCallFingerprint.Of("search", "{\"q\":\"x\"}");
        var other = ToolCallFingerprint.Of("search", "{\"q\":\"y\"}");

        for (var i = 0; i < 3; i++)
        {
            guard.Check(new ToolCall(_a, first));
        }

        Assert.False(guard.Check(new ToolCall(_a, other)).Tripped);

        for (var i = 0; i < 3; i++)
        {
            Assert.False(guard.Check(new ToolCall(_a, first)).Tripped);
        }
    }

    [Fact]
    public void Tool_results_do_not_reset_the_repeat_run()
    {
        var guard = new LoopGuardFacade(new FakeClock());
        var fingerprint = ToolCallFingerprint.Of("search", "{}");
        for (var i = 0; i < 3; i++)
        {
            guard.Check(new ToolCall(_a, fingerprint));
            guard.Check(new ToolResult(_gc));
        }

        Assert.Equal(GuardKind.RepeatToolCall, guard.Check(new ToolCall(_a, fingerprint)).Kind);
    }

    [Fact]
    public void Repeat_runs_are_per_agent()
    {
        var guard = new LoopGuardFacade(new FakeClock());
        var fingerprint = ToolCallFingerprint.Of("search", "{}");
        for (var i = 0; i < 3; i++)
        {
            guard.Check(new ToolCall(_a, fingerprint));
        }

        Assert.False(guard.Check(new ToolCall(_b, fingerprint)).Tripped);
    }

    [Fact]
    public void Identical_arguments_give_identical_fingerprints()
    {
        Assert.Equal(
            ToolCallFingerprint.Of("search", "{\"q\":\"x\"}"),
            ToolCallFingerprint.Of("search", "{\"q\":\"x\"}"));
    }

    [Fact]
    public void Different_tool_or_arguments_give_different_fingerprints()
    {
        var baseline = ToolCallFingerprint.Of("search", "{\"q\":\"x\"}");

        Assert.NotEqual(baseline, ToolCallFingerprint.Of("search", "{\"q\":\"y\"}"));
        Assert.NotEqual(baseline, ToolCallFingerprint.Of("fetch", "{\"q\":\"x\"}"));
    }
}
