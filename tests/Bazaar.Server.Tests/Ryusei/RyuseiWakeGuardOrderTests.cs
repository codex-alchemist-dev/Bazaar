using Bazaar.RyuseiFuda.Orrery;

namespace Bazaar.Server.Tests.Ryusei;

/// <summary>
/// The shared wake guards (doc 38, WP-R6): cooldown, then the daily fire cap, then the shared wake cap. The tick and the event
/// bridge both call <see cref="RyuseiGuards.CheckWake"/>, so the order is fixed in one place. Pure; no clock or database.
/// </summary>
public sealed class RyuseiWakeGuardOrderTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_wake_with_no_history_is_allowed()
    {
        var decision = RyuseiGuards.CheckWake(_now, null, 0, 0, new RyuseiLimits());

        Assert.True(decision.IsAllowed);
    }

    [Fact]
    public void The_cooldown_is_checked_before_the_daily_cap_and_the_wake_cap()
    {
        var limits = new RyuseiLimits();

        var decision = RyuseiGuards.CheckWake(
            _now,
            _now.AddSeconds(-1),
            limits.MaxFiresPerDay,
            limits.MaxAgentWakesPerHour,
            limits);

        Assert.Equal(RyuseiRefusal.CooldownActive, decision.Refusal);
    }

    [Fact]
    public void The_daily_cap_is_checked_before_the_wake_cap()
    {
        var limits = new RyuseiLimits();

        var decision = RyuseiGuards.CheckWake(
            _now,
            _now.AddHours(-1),
            limits.MaxFiresPerDay,
            limits.MaxAgentWakesPerHour,
            limits);

        Assert.Equal(RyuseiRefusal.DailyFireCapReached, decision.Refusal);
    }

    [Fact]
    public void The_wake_cap_refuses_when_the_earlier_guards_pass()
    {
        var limits = new RyuseiLimits();

        var decision = RyuseiGuards.CheckWake(_now, _now.AddHours(-1), 0, limits.MaxAgentWakesPerHour, limits);

        Assert.Equal(RyuseiRefusal.AgentWakeCapReached, decision.Refusal);
    }

    [Fact]
    public void A_wake_just_under_every_cap_is_allowed()
    {
        var limits = new RyuseiLimits();

        var decision = RyuseiGuards.CheckWake(
            _now,
            _now.AddSeconds(-limits.CooldownSeconds),
            limits.MaxFiresPerDay - 1,
            limits.MaxAgentWakesPerHour - 1,
            limits);

        Assert.True(decision.IsAllowed);
    }

    [Fact]
    public void A_null_limits_value_is_refused_at_the_call()
    {
        Assert.Throws<ArgumentNullException>(() => RyuseiGuards.CheckWake(_now, null, 0, 0, null!));
    }
}
