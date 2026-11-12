using Bazaar.RyuseiFuda.Orrery;
using Microsoft.Extensions.Configuration;

namespace Bazaar.Server.Tests.Ryusei;

/// <summary>
/// The RyuseiFuda guards and loop trips as pure functions (doc 38, Loop and runaway safeguards, WP-R8): chain depth, cooldown,
/// single flight, the shared wake cap, the daily cap, auto-pause, the trip record and the Psych Ward hook default. The floors are
/// checked here too, because a setting may tighten a guard but never loosen it past its floor. No database and no clock.
/// </summary>
public sealed class RyuseiGuardTripTests
{
    private static readonly DateTimeOffset _anchor = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid _project = Guid.Parse("11111111-1111-7111-8111-111111111111");
    private static readonly Guid _owner = Guid.Parse("dddddddd-dddd-7ddd-8ddd-dddddddddddd");
    private static readonly Guid _agent = Guid.Parse("aaaaaaaa-aaaa-7aaa-8aaa-aaaaaaaaaaaa");
    private static readonly RyuseiLimits _limits = new();

    private static RyuseiTriggerRecord Row(RyuseiTriggerSpec spec, DateTimeOffset nextFireAt, int chainDepth = 0, int consecutiveRefusals = 0) => new(
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
        ChainDepth: chainDepth,
        Version: 1,
        CreatedAt: _anchor,
        UpdatedAt: _anchor);

    private static RyuseiTriggerRecord Timer(DateTimeOffset fireAt, int chainDepth = 0, int consecutiveRefusals = 0) =>
        Row(new RyuseiTimerTriggerSpec(fireAt), fireAt, chainDepth, consecutiveRefusals);

    private static RyuseiTriggerRecord Hourly(DateTimeOffset nextFireAt, int consecutiveRefusals = 0) =>
        Row(new RyuseiIntervalTriggerSpec(TimeSpan.FromHours(1), _anchor), nextFireAt, consecutiveRefusals: consecutiveRefusals);

    private static RyuseiTickInput Input(
        RyuseiTriggerRecord row,
        IReadOnlyList<RyuseiFireTarget>? targets = null,
        int firesInLast24Hours = 0,
        DateTimeOffset? lastWokeAt = null,
        int wakesInLastHour = 0,
        bool fireInFlight = false) => new(
            row,
            RyuseiSpecJson.Decode(row.Kind, row.SpecJson),
            TimeZoneInfo.Utc,
            targets ?? [new RyuseiFireTarget(_agent, RyuseiFireTargetStatus.Wakeable)],
            firesInLast24Hours,
            lastWokeAt,
            wakesInLastHour,
            fireInFlight);

    private static RyuseiFireDecision? Decide(RyuseiTickInput input, DateTimeOffset now, RyuseiLimits? limits = null) =>
        RyuseiTickRules.Decide(input, now, limits ?? _limits);

    // Chain depth.

    [Fact]
    public void A_trigger_at_the_chain_limit_still_wakes()
    {
        var fireAt = _anchor.AddHours(1);

        var decision = Decide(Input(Timer(fireAt, chainDepth: 3)), fireAt);

        Assert.NotNull(decision);
        Assert.Equal(RyuseiFireOutcome.Woke, decision.Outcome);
        Assert.False(decision.IsTrip);
    }

    [Fact]
    public void A_trigger_past_the_chain_limit_is_a_loop_trip_that_pauses_at_once()
    {
        var fireAt = _anchor.AddHours(1);
        var limits = _limits with { MaxChainDepth = 2 };

        var decision = Decide(Input(Timer(fireAt, chainDepth: 3)), fireAt, limits);

        Assert.NotNull(decision);
        Assert.Equal(RyuseiFireOutcome.RefusedLoop, decision.Outcome);
        Assert.Equal(RyuseiTriggerState.Paused, decision.NextState);
        Assert.Equal(RyuseiRefusal.ChainDepthExceeded, decision.Trip);
        Assert.True(decision.IsTrip);
        Assert.Empty(decision.WakeAgentIds);
        Assert.Equal(1, decision.ConsecutiveRefusals);
        Assert.Equal("ChainDepthExceeded", decision.Cause["refusal"]!.GetValue<string>());
        Assert.True(decision.Cause["autoPaused"]!.GetValue<bool>());
    }

    [Fact]
    public void A_chain_limit_setting_above_the_ceiling_is_clamped_to_three()
    {
        var fireAt = _anchor.AddHours(1);
        var limits = _limits with { MaxChainDepth = 99 };

        var decision = Decide(Input(Timer(fireAt, chainDepth: 3)), fireAt, limits);

        Assert.NotNull(decision);
        Assert.Equal(RyuseiFireOutcome.Woke, decision.Outcome);
    }

    [Fact]
    public void A_chain_limit_setting_below_the_floor_is_clamped_to_one()
    {
        var fireAt = _anchor.AddHours(1);
        var limits = _limits with { MaxChainDepth = 0 };

        Assert.Equal(RyuseiFireOutcome.Woke, Decide(Input(Timer(fireAt, chainDepth: 1)), fireAt, limits)!.Outcome);
        Assert.Equal(RyuseiFireOutcome.RefusedLoop, Decide(Input(Timer(fireAt, chainDepth: 2)), fireAt, limits)!.Outcome);
    }

    [Fact]
    public void A_deep_trigger_whose_targets_are_all_resting_is_skipped_not_tripped()
    {
        var fireAt = _anchor.AddHours(1);
        var resting = new[] { new RyuseiFireTarget(_agent, RyuseiFireTargetStatus.Resting) };

        var decision = Decide(Input(Timer(fireAt, chainDepth: 3), resting), fireAt, _limits with { MaxChainDepth = 1 });

        Assert.NotNull(decision);
        Assert.Equal(RyuseiFireOutcome.SkippedResting, decision.Outcome);
        Assert.False(decision.IsTrip);
    }

    // Cooldown.

    [Fact]
    public void A_cooldown_refusal_is_counted_but_does_not_pause_on_its_own()
    {
        var fireAt = _anchor.AddHours(1);
        var lastWoke = fireAt.AddSeconds(-30);

        var decision = Decide(Input(Hourly(fireAt), lastWokeAt: lastWoke), fireAt);

        Assert.NotNull(decision);
        Assert.Equal(RyuseiFireOutcome.RefusedCooldown, decision.Outcome);
        Assert.Equal(RyuseiTriggerState.Active, decision.NextState);
        Assert.Equal(fireAt.AddHours(1), decision.NextFireAt);
        Assert.Equal(1, decision.ConsecutiveRefusals);
        Assert.False(decision.IsTrip);
    }

    [Fact]
    public void The_third_consecutive_refusal_auto_pauses_the_trigger_as_a_trip()
    {
        var fireAt = _anchor.AddHours(1);
        var lastWoke = fireAt.AddSeconds(-30);

        var decision = Decide(Input(Timer(fireAt, consecutiveRefusals: 2), lastWokeAt: lastWoke), fireAt);

        Assert.NotNull(decision);
        Assert.Equal(RyuseiFireOutcome.RefusedCooldown, decision.Outcome);
        Assert.Equal(RyuseiTriggerState.Paused, decision.NextState);
        Assert.Equal(3, decision.ConsecutiveRefusals);
        Assert.Equal(RyuseiRefusal.AutoPauseTripped, decision.Trip);
    }

    [Fact]
    public void A_cooldown_setting_below_the_ten_second_floor_is_raised_to_the_floor()
    {
        var fireAt = _anchor.AddHours(1);
        var limits = _limits with { CooldownSeconds = 1 };

        var decision = Decide(Input(Timer(fireAt), lastWokeAt: fireAt.AddSeconds(-5)), fireAt, limits);

        Assert.NotNull(decision);
        Assert.Equal(RyuseiFireOutcome.RefusedCooldown, decision.Outcome);
    }

    // Daily fire cap.

    [Fact]
    public void Reaching_the_daily_fire_cap_pauses_the_trigger_at_once_as_a_trip()
    {
        var fireAt = _anchor.AddHours(1);

        var decision = Decide(Input(Timer(fireAt), firesInLast24Hours: 96), fireAt);

        Assert.NotNull(decision);
        Assert.Equal(RyuseiFireOutcome.RefusedCap, decision.Outcome);
        Assert.Equal(RyuseiTriggerState.Paused, decision.NextState);
        Assert.Equal(RyuseiRefusal.DailyFireCapReached, decision.Trip);
    }

    // Shared wake cap.

    [Fact]
    public void The_shared_wake_cap_refuses_without_pausing_or_losing_the_trigger()
    {
        var fireAt = _anchor.AddHours(1);

        var decision = Decide(Input(Hourly(fireAt), wakesInLastHour: 30), fireAt);

        Assert.NotNull(decision);
        Assert.Equal(RyuseiFireOutcome.RefusedCap, decision.Outcome);
        Assert.Equal(RyuseiTriggerState.Active, decision.NextState);
        Assert.Equal(1, decision.ConsecutiveRefusals);
        Assert.False(decision.IsTrip);
        Assert.Equal("AgentWakeCapReached", decision.Cause["refusal"]!.GetValue<string>());
    }

    [Fact]
    public void A_timer_refused_three_times_in_a_row_is_paused_as_a_trip()
    {
        var fireAt = _anchor.AddHours(1);

        var decision = Decide(Input(Timer(fireAt, consecutiveRefusals: 2), wakesInLastHour: 30), fireAt);

        Assert.NotNull(decision);
        Assert.Equal(RyuseiTriggerState.Paused, decision.NextState);
        Assert.Equal(RyuseiRefusal.AutoPauseTripped, decision.Trip);
    }

    [Fact]
    public void The_wake_cap_is_read_from_the_shared_runtime_guard_setting()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Bazaar:Runtime:Guard:WakesPerHour"] = "12" })
            .Build();

        Assert.Equal(12, RyuseiScheduleComponent.ResolveLimits(configuration).MaxAgentWakesPerHour);
    }

    [Fact]
    public void Without_a_runtime_guard_setting_the_wake_cap_is_the_shipped_default()
    {
        var configuration = new ConfigurationBuilder().Build();

        Assert.Equal(RyuseiDefaults.MaxAgentWakesPerHour, RyuseiScheduleComponent.ResolveLimits(configuration).MaxAgentWakesPerHour);
    }

    [Fact]
    public void The_runtime_wake_setting_wins_over_a_ryusei_wake_setting()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bazaar:Ryusei:MaxAgentWakesPerHour"] = "5",
                ["Bazaar:Runtime:Guard:WakesPerHour"] = "12",
            })
            .Build();

        Assert.Equal(12, RyuseiScheduleComponent.ResolveLimits(configuration).MaxAgentWakesPerHour);
    }

    // Single flight.

    [Fact]
    public void A_fire_that_finds_another_fire_in_flight_is_skipped_and_moves_nothing()
    {
        var fireAt = _anchor.AddHours(1);
        var row = Timer(fireAt, consecutiveRefusals: 1);

        var decision = Decide(Input(row, fireInFlight: true), fireAt);

        Assert.NotNull(decision);
        Assert.Equal(RyuseiFireOutcome.SkippedInflight, decision.Outcome);
        Assert.Equal(RyuseiTriggerState.Active, decision.NextState);
        Assert.Equal(fireAt, decision.NextFireAt);
        Assert.Equal(1, decision.ConsecutiveRefusals);
        Assert.Empty(decision.WakeAgentIds);
        Assert.False(decision.IsTrip);
    }

    [Fact]
    public void The_single_flight_rule_refuses_only_when_a_fire_is_in_flight()
    {
        Assert.Equal(RyuseiRefusal.SingleFlightHeld, RyuseiGuards.CheckSingleFlight(true).Refusal);
        Assert.True(RyuseiGuards.CheckSingleFlight(false).IsAllowed);
    }

    // The trip record and the Psych Ward hook.

    [Fact]
    public void A_fire_that_wakes_or_skips_is_never_a_trip()
    {
        var fireAt = _anchor.AddHours(1);

        Assert.False(Decide(Input(Timer(fireAt)), fireAt)!.IsTrip);
        Assert.False(Decide(Input(Timer(fireAt), new[] { new RyuseiFireTarget(_agent, RyuseiFireTargetStatus.Retired) }), fireAt)!.IsTrip);
    }

    [Theory]
    [InlineData(RyuseiRefusal.ChainDepthExceeded, "ryusei_chain_depth")]
    [InlineData(RyuseiRefusal.DailyFireCapReached, "ryusei_daily_fire_cap")]
    [InlineData(RyuseiRefusal.AutoPauseTripped, "ryusei_refusal_streak")]
    public void Each_trip_has_a_guard_event_kind(RyuseiRefusal trip, string kind) =>
        Assert.Equal(kind, RyuseiTickRules.GuardEventKind(trip));

    [Theory]
    [InlineData(RyuseiRefusal.CooldownActive)]
    [InlineData(RyuseiRefusal.AgentWakeCapReached)]
    [InlineData(RyuseiRefusal.None)]
    public void A_refusal_that_does_not_pause_is_not_a_trip_kind(RyuseiRefusal notATrip) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => RyuseiTickRules.GuardEventKind(notATrip));

    [Fact]
    public void The_new_outcomes_map_to_the_fire_table_values()
    {
        Assert.Equal("refused_loop", RyuseiTickRules.ToSql(RyuseiFireOutcome.RefusedLoop));
        Assert.Equal("skipped_inflight", RyuseiTickRules.ToSql(RyuseiFireOutcome.SkippedInflight));
    }

    [Fact]
    public async Task The_default_psych_ward_hook_admits_nobody_and_accepts_a_trip()
    {
        NoOpPsychWardAdmission ward = new();
        var admission = new PsychWardAdmission(Guid.CreateVersion7(), RyuseiRefusal.ChainDepthExceeded, "ryusei_chain_depth", [_agent]);

        await ward.AdmitAsync(admission, TestContext.Current.CancellationToken);

        Assert.Equal(RyuseiRefusal.ChainDepthExceeded, admission.Trip);
        await Assert.ThrowsAsync<ArgumentNullException>(() => ward.AdmitAsync(null!, TestContext.Current.CancellationToken));
    }
}
