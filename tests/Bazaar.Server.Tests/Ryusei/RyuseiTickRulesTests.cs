using Bazaar.Server.Agents.Inbox;
using Bazaar.RyuseiFuda.Orrery;

namespace Bazaar.Server.Tests.Ryusei;

/// <summary>
/// The scheduler tick's decisions as pure functions (doc 38, WP-R5): which due triggers fire, skip or get refused, how each row
/// changes, and which wake kind and fire outcome text each decision uses. No database and no clock.
/// </summary>
public sealed class RyuseiTickRulesTests
{
    private static readonly DateTimeOffset _anchor = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid _project = Guid.Parse("11111111-1111-7111-8111-111111111111");
    private static readonly Guid _owner = Guid.Parse("dddddddd-dddd-7ddd-8ddd-dddddddddddd");
    private static readonly Guid _agent = Guid.Parse("aaaaaaaa-aaaa-7aaa-8aaa-aaaaaaaaaaaa");
    private static readonly Guid _second = Guid.Parse("bbbbbbbb-bbbb-7bbb-8bbb-bbbbbbbbbbbb");
    private static readonly RyuseiLimits _limits = new();

    private static RyuseiTimerTriggerSpec Timer(DateTimeOffset fireAt) => new(fireAt);

    private static RyuseiIntervalTriggerSpec Hourly() => new(TimeSpan.FromHours(1), _anchor);

    private static RyuseiTriggerRecord Row(
        RyuseiTriggerSpec spec,
        DateTimeOffset? nextFireAt,
        RyuseiTriggerState state = RyuseiTriggerState.Active,
        long fireCount = 0,
        int consecutiveRefusals = 0,
        Guid[]? targets = null) => new(
            Guid.CreateVersion7(),
            _project,
            _owner,
            spec.Kind,
            targets ?? [_agent],
            "check the build",
            RyuseiSpecJson.Encode(spec),
            TaskId: null,
            AttachGcId: null,
            RyuseiPingMode.None,
            state,
            nextFireAt,
            fireCount,
            consecutiveRefusals,
            MaxFires: null,
            ExpiresAt: null,
            ChainDepth: 0,
            Version: 1,
            CreatedAt: _anchor,
            UpdatedAt: _anchor);

    private static RyuseiFireTarget Wakeable(Guid agent) => new(agent, RyuseiFireTargetStatus.Wakeable);

    private static RyuseiTickInput Input(
        RyuseiTriggerRecord row,
        IReadOnlyList<RyuseiFireTarget>? targets = null,
        int firesInLast24Hours = 0,
        DateTimeOffset? lastWokeAt = null) => new(
            row,
            RyuseiSpecJson.Decode(row.Kind, row.SpecJson),
            TimeZoneInfo.Utc,
            targets ?? row.TargetAgentIds.Select(Wakeable).ToArray(),
            firesInLast24Hours,
            lastWokeAt);

    private static RyuseiFireDecision? Decide(RyuseiTickInput input, DateTimeOffset now) => RyuseiTickRules.Decide(input, now, _limits);

    // Due selection and the one-shot timer.

    [Fact]
    public void A_due_timer_wakes_its_target_once_and_is_then_fired()
    {
        var fireAt = _anchor.AddHours(1);
        var row = Row(Timer(fireAt), fireAt);

        var decision = Decide(Input(row), fireAt.AddSeconds(5));

        Assert.NotNull(decision);
        Assert.Equal(RyuseiFireOutcome.Woke, decision.Outcome);
        Assert.Equal(fireAt, decision.ScheduledFor);
        Assert.Equal(RyuseiTriggerState.Fired, decision.NextState);
        Assert.Null(decision.NextFireAt);
        Assert.Equal(new[] { _agent }, decision.WakeAgentIds);
        Assert.Equal(InboxKind.RyuseiTimer, decision.WakeReason);
        Assert.Equal(0L, decision.Cause["missedCount"]!.GetValue<long>());
    }

    [Fact]
    public void A_timer_that_is_long_past_fires_once_with_no_missed_count()
    {
        var fireAt = _anchor.AddDays(-3);
        var row = Row(Timer(fireAt), fireAt);

        var decision = Decide(Input(row), _anchor);

        Assert.NotNull(decision);
        Assert.Equal(RyuseiFireOutcome.Woke, decision.Outcome);
        Assert.Equal(fireAt, decision.ScheduledFor);
        Assert.Equal(0L, decision.Cause["missedCount"]!.GetValue<long>());
    }

    [Fact]
    public void A_timer_that_is_not_yet_due_is_left_alone()
    {
        var fireAt = _anchor.AddHours(1);
        var row = Row(Timer(fireAt), fireAt);

        Assert.Null(Decide(Input(row), fireAt.AddSeconds(-1)));
    }

    [Theory]
    [InlineData(RyuseiTriggerState.Paused)]
    [InlineData(RyuseiTriggerState.Cancelled)]
    [InlineData(RyuseiTriggerState.Fired)]
    [InlineData(RyuseiTriggerState.Expired)]
    public void Triggers_that_are_not_active_are_skipped(RyuseiTriggerState state)
    {
        var fireAt = _anchor.AddHours(1);
        var row = Row(Timer(fireAt), fireAt, state);

        Assert.Null(Decide(Input(row), fireAt.AddMinutes(10)));
    }

    [Fact]
    public void An_event_is_never_tickable_and_an_idle_watcher_is_not_due()
    {
        var watcher = new RyuseiWatcherTriggerSpec(new RyuseiPredicate { Kind = "task.transitioned" }, 60, 30, null, null);
        var evt = new RyuseiEventTriggerSpec(new RyuseiPredicate { Kind = "task.transitioned" });

        // An idle watcher has no next fire (WP-R6 arms it on a match), so it is not due at any time.
        Assert.Null(Decide(Input(Row(watcher, null)), _anchor.AddHours(1)));
        Assert.Null(Decide(Input(Row(evt, null)), _anchor.AddHours(1)));
        Assert.True(RyuseiTickRules.IsTickable(RyuseiTriggerKind.Watcher));
        Assert.False(RyuseiTickRules.IsTickable(RyuseiTriggerKind.Event));
        Assert.True(RyuseiTickRules.IsTickable(RyuseiTriggerKind.Timer));
        Assert.True(RyuseiTickRules.IsTickable(RyuseiTriggerKind.Interval));
    }

    [Fact]
    public void An_armed_watcher_fires_as_a_watcher_once_its_deadline_passes_and_then_goes_idle()
    {
        var watcher = new RyuseiWatcherTriggerSpec(new RyuseiPredicate { Kind = "task.transitioned" }, 60, 30, null, null);
        var deadline = _anchor.AddSeconds(30);
        var row = Row(watcher, deadline);

        Assert.Null(Decide(Input(row), deadline.AddSeconds(-1)));

        var decision = Decide(Input(row), deadline.AddSeconds(1));

        Assert.NotNull(decision);
        Assert.Equal(RyuseiFireOutcome.Woke, decision.Outcome);
        Assert.Equal(deadline, decision.ScheduledFor);
        Assert.Equal(RyuseiTriggerState.Active, decision.NextState);
        Assert.Null(decision.NextFireAt);
        Assert.Equal("woke", RyuseiTickRules.ToSql(RyuseiFireOutcome.Woke));
        Assert.Equal("ryusei_watcher", decision.WakeReason);
        Assert.Single(decision.WakeAgentIds);
        Assert.Equal(_agent, decision.WakeAgentIds[0]);
    }

    [Fact]
    public void A_watcher_that_reaches_max_fires_is_fired_by_its_last_wake()
    {
        var watcher = new RyuseiWatcherTriggerSpec(new RyuseiPredicate { Kind = "task.transitioned" }, 60, 30, null, 2);
        var deadline = _anchor.AddSeconds(30);
        var row = Row(watcher, deadline, fireCount: 1) with { MaxFires = 2 };

        var decision = Decide(Input(row), deadline.AddSeconds(1));

        Assert.NotNull(decision);
        Assert.Equal(RyuseiFireOutcome.Woke, decision.Outcome);
        Assert.Equal(RyuseiTriggerState.Fired, decision.NextState);
    }

    [Fact]
    public void An_armed_watcher_inside_the_cooldown_is_refused_and_goes_idle()
    {
        var watcher = new RyuseiWatcherTriggerSpec(new RyuseiPredicate { Kind = "task.transitioned" }, 60, 30, null, null);
        var deadline = _anchor.AddSeconds(30);
        var row = Row(watcher, deadline, fireCount: 1);

        var decision = Decide(Input(row, lastWokeAt: deadline.AddSeconds(-5)), deadline);

        Assert.NotNull(decision);
        Assert.Equal(RyuseiFireOutcome.RefusedCooldown, decision.Outcome);
        Assert.Null(decision.NextFireAt);
        Assert.Equal(RyuseiTriggerState.Active, decision.NextState);
    }

    [Fact]
    public void An_armed_watcher_with_no_wakeable_target_is_skipped()
    {
        var watcher = new RyuseiWatcherTriggerSpec(new RyuseiPredicate { Kind = "task.transitioned" }, 60, 30, null, null);
        var deadline = _anchor.AddSeconds(30);
        var row = Row(watcher, deadline);

        var decision = Decide(Input(row, targets: [new RyuseiFireTarget(_agent, RyuseiFireTargetStatus.Resting)]), deadline);

        Assert.NotNull(decision);
        Assert.Equal(RyuseiFireOutcome.SkippedResting, decision.Outcome);
        Assert.Equal(RyuseiTriggerState.Active, decision.NextState);
    }

    // Interval arithmetic: missed fires coalesce into one.

    [Fact]
    public void A_new_interval_that_is_overdue_fires_once_at_the_latest_occurrence()
    {
        var row = Row(Hourly(), _anchor);

        var decision = Decide(Input(row), _anchor.AddHours(3).AddMinutes(30));

        Assert.NotNull(decision);
        Assert.Equal(RyuseiFireOutcome.Woke, decision.Outcome);
        Assert.Equal(_anchor.AddHours(3), decision.ScheduledFor);
        Assert.Equal(3L, decision.Cause["missedCount"]!.GetValue<long>());
        Assert.Equal(RyuseiTriggerState.Active, decision.NextState);
        Assert.Equal(_anchor.AddHours(4), decision.NextFireAt);
        Assert.Equal(InboxKind.RyuseiInterval, decision.WakeReason);
    }

    [Fact]
    public void After_a_fire_the_interval_resumes_from_the_next_unhandled_occurrence()
    {
        // Fired at 15:00. The stored next fire is 16:00. Now it is 18:20, so 16:00, 17:00 and 18:00 are due and coalesce.
        var row = Row(Hourly(), _anchor.AddHours(4), fireCount: 1);
        var lastWoke = _anchor.AddHours(3);

        var decision = Decide(Input(row, lastWokeAt: lastWoke), _anchor.AddHours(6).AddMinutes(20));

        Assert.NotNull(decision);
        Assert.Equal(RyuseiFireOutcome.Woke, decision.Outcome);
        Assert.Equal(_anchor.AddHours(6), decision.ScheduledFor);
        Assert.Equal(2L, decision.Cause["missedCount"]!.GetValue<long>());
        Assert.Equal(_anchor.AddHours(7), decision.NextFireAt);
    }

    [Fact]
    public void An_interval_whose_next_occurrence_is_still_ahead_is_left_alone()
    {
        var row = Row(Hourly(), _anchor.AddHours(4), fireCount: 1);

        Assert.Null(Decide(Input(row, lastWokeAt: _anchor.AddHours(3)), _anchor.AddHours(4).AddSeconds(-1)));
    }

    [Fact]
    public void An_interval_below_the_floor_is_malformed_and_pauses()
    {
        var row = Row(new RyuseiIntervalTriggerSpec(TimeSpan.FromSeconds(30), _anchor), _anchor);

        var decision = Decide(Input(row), _anchor.AddMinutes(1));

        Assert.NotNull(decision);
        Assert.Equal(RyuseiFireOutcome.Error, decision.Outcome);
        Assert.Equal(RyuseiTriggerState.Paused, decision.NextState);
        Assert.Equal(_anchor, decision.NextFireAt);
        Assert.Equal(_anchor, decision.ScheduledFor);
        Assert.Empty(decision.WakeAgentIds);
    }

    [Fact]
    public void A_malformed_trigger_keeps_its_schedule_and_pauses_for_the_owner()
    {
        var row = Row(Timer(_anchor), _anchor);

        var decision = RyuseiTickRules.Malformed(row, "The stored spec does not decode.");

        Assert.Equal(RyuseiFireOutcome.Error, decision.Outcome);
        Assert.Equal(RyuseiTriggerState.Paused, decision.NextState);
        Assert.Equal(_anchor, decision.NextFireAt);
        Assert.Equal("The stored spec does not decode.", decision.Cause["message"]!.GetValue<string>());
    }

    // Guards: cooldown, daily cap, auto-pause.

    [Fact]
    public void A_fire_inside_the_cooldown_is_refused_and_still_moves_the_schedule_on()
    {
        var now = _anchor.AddHours(1);
        var row = Row(Hourly(), now, fireCount: 2);

        var decision = Decide(Input(row, lastWokeAt: now.AddSeconds(-30)), now);

        Assert.NotNull(decision);
        Assert.Equal(RyuseiFireOutcome.RefusedCooldown, decision.Outcome);
        Assert.Equal(now, decision.ScheduledFor);
        Assert.Equal(RyuseiTriggerState.Active, decision.NextState);
        Assert.Equal(now.AddHours(1), decision.NextFireAt);
        Assert.Equal(1, decision.ConsecutiveRefusals);
        Assert.Empty(decision.WakeAgentIds);
        Assert.Null(decision.WakeReason);
    }

    [Fact]
    public void Reaching_the_daily_cap_refuses_and_pauses_the_trigger()
    {
        var now = _anchor.AddHours(1);
        var row = Row(Hourly(), now, fireCount: 96);

        var decision = Decide(Input(row, firesInLast24Hours: _limits.MaxFiresPerDay), now);

        Assert.NotNull(decision);
        Assert.Equal(RyuseiFireOutcome.RefusedCap, decision.Outcome);
        Assert.Equal(RyuseiTriggerState.Paused, decision.NextState);
        Assert.Equal(now.AddHours(1), decision.NextFireAt);
        Assert.Equal(1, decision.ConsecutiveRefusals);
        Assert.Equal("DailyFireCapReached", decision.Cause["refusal"]!.GetValue<string>());
    }

    [Fact]
    public void One_below_the_daily_cap_still_wakes()
    {
        var now = _anchor.AddHours(1);
        var row = Row(Hourly(), now, fireCount: 95);

        var decision = Decide(Input(row, firesInLast24Hours: _limits.MaxFiresPerDay - 1), now);

        Assert.NotNull(decision);
        Assert.Equal(RyuseiFireOutcome.Woke, decision.Outcome);
    }

    [Fact]
    public void Refusals_pause_the_trigger_at_the_auto_pause_threshold()
    {
        var now = _anchor.AddHours(1);
        var row = Row(Hourly(), now, fireCount: 2, consecutiveRefusals: _limits.AutoPauseAfter - 1);

        var decision = Decide(Input(row, lastWokeAt: now.AddSeconds(-30)), now);

        Assert.NotNull(decision);
        Assert.Equal(RyuseiFireOutcome.RefusedCooldown, decision.Outcome);
        Assert.Equal(RyuseiTriggerState.Paused, decision.NextState);
        Assert.Equal(_limits.AutoPauseAfter, decision.ConsecutiveRefusals);
        Assert.True(decision.Cause["autoPaused"]!.GetValue<bool>());
    }

    [Fact]
    public void A_refusal_below_the_auto_pause_threshold_keeps_the_trigger_active()
    {
        var now = _anchor.AddHours(1);
        var row = Row(Hourly(), now, fireCount: 2, consecutiveRefusals: 0);

        var decision = Decide(Input(row, lastWokeAt: now.AddSeconds(-30)), now);

        Assert.NotNull(decision);
        Assert.Equal(RyuseiTriggerState.Active, decision.NextState);
        Assert.Equal(1, decision.ConsecutiveRefusals);
    }

    [Fact]
    public void A_woke_fire_clears_the_refusal_count()
    {
        var now = _anchor.AddHours(1);
        var row = Row(Hourly(), now, fireCount: 2, consecutiveRefusals: 2);

        var decision = Decide(Input(row), now);

        Assert.NotNull(decision);
        Assert.Equal(RyuseiFireOutcome.Woke, decision.Outcome);
        Assert.Equal(0, decision.ConsecutiveRefusals);
    }

    // Targets: resting, retired and missing agents.

    [Fact]
    public void A_fire_whose_only_target_is_resting_is_skipped_and_keeps_its_schedule()
    {
        var now = _anchor.AddHours(1);
        var row = Row(Hourly(), now, fireCount: 2, consecutiveRefusals: 2);
        var targets = new[] { new RyuseiFireTarget(_agent, RyuseiFireTargetStatus.Resting) };

        var decision = Decide(Input(row, targets), now);

        Assert.NotNull(decision);
        Assert.Equal(RyuseiFireOutcome.SkippedResting, decision.Outcome);
        Assert.Equal(RyuseiTriggerState.Active, decision.NextState);
        Assert.Equal(now.AddHours(1), decision.NextFireAt);
        Assert.Equal(2, decision.ConsecutiveRefusals);
        Assert.Empty(decision.WakeAgentIds);
    }

    [Theory]
    [InlineData(RyuseiFireTargetStatus.Retired)]
    [InlineData(RyuseiFireTargetStatus.Missing)]
    public void A_trigger_whose_targets_are_all_retired_or_missing_expires(RyuseiFireTargetStatus status)
    {
        var fireAt = _anchor.AddHours(1);
        var row = Row(Timer(fireAt), fireAt);
        var targets = new[] { new RyuseiFireTarget(_agent, status) };

        var decision = Decide(Input(row, targets), fireAt);

        Assert.NotNull(decision);
        Assert.Equal(RyuseiFireOutcome.SkippedResting, decision.Outcome);
        Assert.Equal(RyuseiTriggerState.Expired, decision.NextState);
        Assert.Null(decision.NextFireAt);
    }

    [Fact]
    public void A_partly_resting_fire_wakes_only_the_wakeable_targets_and_lists_the_rest()
    {
        var now = _anchor.AddHours(1);
        var row = Row(Hourly(), now, targets: [_agent, _second]);
        var targets = new[]
        {
            Wakeable(_agent),
            new RyuseiFireTarget(_second, RyuseiFireTargetStatus.Resting),
        };

        var decision = Decide(Input(row, targets), now);

        Assert.NotNull(decision);
        Assert.Equal(RyuseiFireOutcome.Woke, decision.Outcome);
        Assert.Equal(new[] { _agent }, decision.WakeAgentIds);
        var skipped = Assert.IsType<System.Text.Json.Nodes.JsonArray>(decision.Cause["skippedAgents"]);
        var only = Assert.Single(skipped);
        Assert.Equal(_second.ToString("D"), only!["agentId"]!.GetValue<string>());
        Assert.Equal("resting", only["status"]!.GetValue<string>());
    }

    // Wake kinds and outcome text.

    [Theory]
    [InlineData(RyuseiTriggerKind.Timer, "ryusei_timer")]
    [InlineData(RyuseiTriggerKind.Interval, "ryusei_interval")]
    [InlineData(RyuseiTriggerKind.Watcher, "ryusei_watcher")]
    [InlineData(RyuseiTriggerKind.Event, "ryusei_event")]
    public void Each_trigger_kind_has_its_wake_reason_and_the_inbox_knows_it(RyuseiTriggerKind kind, string reason)
    {
        Assert.Equal(reason, RyuseiTickRules.WakeReason(kind));
        Assert.True(InboxPriority.IsKnown(reason));
    }

    [Theory]
    [InlineData(RyuseiFireOutcome.Woke, "woke")]
    [InlineData(RyuseiFireOutcome.RefusedCap, "refused_cap")]
    [InlineData(RyuseiFireOutcome.RefusedCooldown, "refused_cooldown")]
    [InlineData(RyuseiFireOutcome.SkippedResting, "skipped_resting")]
    [InlineData(RyuseiFireOutcome.Error, "error")]
    public void Each_outcome_maps_to_its_stored_text(RyuseiFireOutcome outcome, string text)
    {
        Assert.Equal(text, RyuseiTickRules.ToSql(outcome));
    }
}
