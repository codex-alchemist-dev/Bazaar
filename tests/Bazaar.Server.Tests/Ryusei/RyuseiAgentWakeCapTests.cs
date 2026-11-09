using Bazaar.Server.Kanban.Ryusei;

namespace Bazaar.Server.Tests.Ryusei;

/// <summary>
/// The per-agent wake cap (doc 38, Loop and runaway safeguards; shared with doc 06). A fire that hits the cap is recorded as
/// refused_cap and the trigger is not paused, so the trigger is not lost. Pure: no database and no clock.
/// </summary>
public sealed class RyuseiAgentWakeCapTests
{
    private static readonly DateTimeOffset _anchor = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid _project = Guid.Parse("11111111-1111-7111-8111-111111111111");
    private static readonly Guid _owner = Guid.Parse("dddddddd-dddd-7ddd-8ddd-dddddddddddd");
    private static readonly Guid _agent = Guid.Parse("aaaaaaaa-aaaa-7aaa-8aaa-aaaaaaaaaaaa");
    private static readonly RyuseiLimits _limits = new();

    private static RyuseiTriggerRecord Row(RyuseiTriggerSpec spec, DateTimeOffset nextFireAt, int consecutiveRefusals = 0) => new(
        Guid.CreateVersion7(),
        _project,
        _owner,
        spec.Kind,
        [_agent],
        "check the build",
        RyuseiSpecJson.Encode(spec),
        TaskId: null,
        AttachGcId: null,
        RyuseiPingMode.None,
        RyuseiTriggerState.Active,
        nextFireAt,
        FireCount: 0,
        consecutiveRefusals,
        MaxFires: null,
        ExpiresAt: null,
        ChainDepth: 0,
        Version: 1,
        CreatedAt: _anchor,
        UpdatedAt: _anchor);

    private static RyuseiTickInput Input(RyuseiTriggerRecord row, int wakesInLastHour, int firesInLast24Hours = 0) => new(
        row,
        RyuseiSpecJson.Decode(row.Kind, row.SpecJson),
        TimeZoneInfo.Utc,
        [new RyuseiFireTarget(_agent, RyuseiFireTargetStatus.Wakeable)],
        firesInLast24Hours,
        LastWokeAt: null,
        WakesInLastHour: wakesInLastHour);

    [Fact]
    public void The_shipped_wake_cap_is_30_per_hour_with_no_floor()
    {
        Assert.Equal(30, _limits.MaxAgentWakesPerHour);
        Assert.Equal(30, RyuseiDefaults.MaxAgentWakesPerHour);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(29)]
    public void Below_the_cap_the_guard_allows(int wakes)
    {
        var decision = RyuseiGuards.CheckAgentWakeCap(wakes, 30);

        Assert.True(decision.IsAllowed);
        Assert.Equal(RyuseiRefusal.None, decision.Refusal);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(31)]
    public void At_or_above_the_cap_the_guard_refuses_with_the_wake_cap_reason(int wakes)
    {
        var decision = RyuseiGuards.CheckAgentWakeCap(wakes, 30);

        Assert.False(decision.IsAllowed);
        Assert.Equal(RyuseiRefusal.AgentWakeCapReached, decision.Refusal);
    }

    [Fact]
    public void A_fire_below_the_wake_cap_still_wakes()
    {
        var now = _anchor.AddHours(1);
        var row = Row(new RyuseiTimerTriggerSpec(now), now);

        var decision = RyuseiTickRules.Decide(Input(row, wakesInLastHour: 29), now, _limits);

        Assert.NotNull(decision);
        Assert.Equal(RyuseiFireOutcome.Woke, decision.Outcome);
        Assert.Equal(RyuseiTriggerState.Fired, decision.NextState);
    }

    [Fact]
    public void A_fire_at_the_wake_cap_is_refused_as_refused_cap_and_is_not_paused()
    {
        var now = _anchor.AddHours(1);
        var row = Row(new RyuseiIntervalTriggerSpec(TimeSpan.FromHours(1), _anchor), now);

        var decision = RyuseiTickRules.Decide(Input(row, wakesInLastHour: 30), now, _limits);

        Assert.NotNull(decision);
        Assert.Equal(RyuseiFireOutcome.RefusedCap, decision.Outcome);
        Assert.Equal(RyuseiTriggerState.Active, decision.NextState);
        Assert.Equal(1, decision.ConsecutiveRefusals);
        Assert.Empty(decision.WakeAgentIds);
        Assert.Equal("AgentWakeCapReached", decision.Cause["refusal"]!.GetValue<string>());
        Assert.False(decision.Cause["autoPaused"]!.GetValue<bool>());
    }

    [Fact]
    public void A_wake_cap_refusal_keeps_the_schedule_moving_so_the_occurrence_is_not_retried()
    {
        var now = _anchor.AddHours(1);
        var nextFireAt = now.AddMinutes(-1);
        var row = Row(new RyuseiIntervalTriggerSpec(TimeSpan.FromHours(1), _anchor), nextFireAt);

        var decision = RyuseiTickRules.Decide(Input(row, wakesInLastHour: 30), now, _limits);

        Assert.NotNull(decision);
        Assert.Equal(RyuseiFireOutcome.RefusedCap, decision.Outcome);
        Assert.NotNull(decision.NextFireAt);
        Assert.True(decision.NextFireAt > now);
    }

    [Fact]
    public void Reaching_the_daily_cap_still_pauses_the_trigger_at_once()
    {
        var now = _anchor.AddHours(1);
        var row = Row(new RyuseiIntervalTriggerSpec(TimeSpan.FromHours(1), _anchor), now);

        var decision = RyuseiTickRules.Decide(
            Input(row, wakesInLastHour: 0, firesInLast24Hours: _limits.MaxFiresPerDay),
            now,
            _limits);

        Assert.NotNull(decision);
        Assert.Equal(RyuseiFireOutcome.RefusedCap, decision.Outcome);
        Assert.Equal(RyuseiTriggerState.Paused, decision.NextState);
        Assert.Equal("DailyFireCapReached", decision.Cause["refusal"]!.GetValue<string>());
        Assert.True(decision.Cause["autoPaused"]!.GetValue<bool>());
    }

    [Fact]
    public void The_daily_cap_is_checked_before_the_wake_cap()
    {
        var now = _anchor.AddHours(1);
        var row = Row(new RyuseiIntervalTriggerSpec(TimeSpan.FromHours(1), _anchor), now);

        var decision = RyuseiTickRules.Decide(
            Input(row, wakesInLastHour: 30, firesInLast24Hours: _limits.MaxFiresPerDay),
            now,
            _limits);

        Assert.NotNull(decision);
        Assert.Equal("DailyFireCapReached", decision.Cause["refusal"]!.GetValue<string>());
        Assert.Equal(RyuseiTriggerState.Paused, decision.NextState);
    }

    [Fact]
    public void A_wake_cap_refusal_pauses_once_the_refusal_streak_reaches_the_threshold()
    {
        var now = _anchor.AddHours(1);
        var row = Row(new RyuseiIntervalTriggerSpec(TimeSpan.FromHours(1), _anchor), now, consecutiveRefusals: _limits.AutoPauseAfter - 1);

        var decision = RyuseiTickRules.Decide(Input(row, wakesInLastHour: 30), now, _limits);

        Assert.NotNull(decision);
        Assert.Equal(RyuseiFireOutcome.RefusedCap, decision.Outcome);
        Assert.Equal(RyuseiTriggerState.Paused, decision.NextState);
        Assert.Equal(_limits.AutoPauseAfter, decision.ConsecutiveRefusals);
        Assert.True(decision.Cause["autoPaused"]!.GetValue<bool>());
    }

    [Fact]
    public void A_lowered_wake_cap_refuses_a_timer_fire_without_pausing_it()
    {
        var limits = new RyuseiLimits { MaxAgentWakesPerHour = 2 };
        var now = _anchor.AddHours(1);
        var row = Row(new RyuseiTimerTriggerSpec(now), now);

        var decision = RyuseiTickRules.Decide(Input(row, wakesInLastHour: 2), now, limits);

        Assert.NotNull(decision);
        Assert.Equal(RyuseiFireOutcome.RefusedCap, decision.Outcome);

        // A one-shot timer is used up by its decided occurrence, refused or not, so it is never retried; it is not paused.
        Assert.Equal(RyuseiTriggerState.Fired, decision.NextState);
    }
}
