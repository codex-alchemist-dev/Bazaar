using Bazaar.RyuseiFuda.Orrery;
using Bazaar.Server.Kanban.Data;

namespace Bazaar.Server.Tests.Ryusei;

/// <summary>
/// The event bridge as pure functions (doc 38, WP-R6): the kind mapping from repo events to the closed predicate set, the scope
/// rule for GC-sourced events, the debounce arithmetic, and the one-shot and watcher decisions. No database and no clock.
/// </summary>
public sealed class RyuseiEventRulesTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid _project = Guid.Parse("11111111-1111-7111-8111-111111111111");
    private static readonly Guid _owner = Guid.Parse("dddddddd-dddd-7ddd-8ddd-dddddddddddd");
    private static readonly Guid _agent = Guid.Parse("aaaaaaaa-aaaa-7aaa-8aaa-aaaaaaaaaaaa");
    private static readonly Guid _other = Guid.Parse("bbbbbbbb-bbbb-7bbb-8bbb-bbbbbbbbbbbb");
    private static readonly Guid _gc = Guid.Parse("22222222-2222-7222-8222-222222222222");
    private static readonly Guid _task = Guid.Parse("33333333-3333-7333-8333-333333333333");

    private static RyuseiTriggerRecord Row(
        RyuseiTriggerSpec spec,
        DateTimeOffset? nextFireAt = null,
        DateTimeOffset? expiresAt = null,
        Guid[]? targets = null) => new(
            Guid.CreateVersion7(),
            _project,
            _owner,
            spec.Kind,
            targets ?? [_agent],
            "watch the board",
            RyuseiSpecJson.Encode(spec),
            TaskId: null,
            AttachGcId: null,
            RyuseiPingMode.None,
            RyuseiTriggerState.Active,
            nextFireAt,
            FireCount: 0,
            ConsecutiveRefusals: 0,
            MaxFires: null,
            ExpiresAt: expiresAt,
            ChainDepth: 0,
            Version: 1,
            CreatedAt: _now,
            UpdatedAt: _now);

    private static RyuseiEventEnvelope TaskEvent(string kind) =>
        RyuseiEventMapping.FromTaskEvent(kind, _project, _task, "{}")!;

    private static RyuseiEventEnvelope GcMessage(Guid gc, string authorKind, Guid author) =>
        RyuseiEventMapping.FromGcEvent("message", gc, authorKind, author, 7)!;

    // Mapping from repo events to predicate kinds.

    [Theory]
    [InlineData(TaskEventKinds.Transitioned, "task.transitioned")]
    [InlineData(TaskEventKinds.DuePassed, "task.due_passed")]
    [InlineData(TaskEventKinds.Blocked, "task.blocked")]
    [InlineData(TaskEventKinds.Unblocked, "task.unblocked")]
    public void Task_events_map_to_their_predicate_wire_names(string taskEventKind, string predicate)
    {
        var mapped = RyuseiEventMapping.FromTaskEvent(taskEventKind, _project, _task, "{\"from\":\"Ready\"}");

        Assert.NotNull(mapped);
        Assert.Equal(predicate, mapped.Kind);
        Assert.Equal(_project, mapped.ProjectId);
        Assert.Equal(_task, mapped.TaskId);
        Assert.Null(mapped.GcId);
        Assert.True(RyuseiPredicates.TryParse(mapped.Kind, out _));
    }

    [Theory]
    [InlineData(TaskEventKinds.Created)]
    [InlineData(TaskEventKinds.EdgeChanged)]
    [InlineData(TaskEventKinds.GuardRefused)]
    [InlineData(TaskEventKinds.GateDecided)]
    [InlineData(TaskEventKinds.GateRecorded)]
    [InlineData(TaskEventKinds.ReviewApproved)]
    public void Task_events_outside_the_closed_set_are_not_mapped(string taskEventKind)
    {
        Assert.Null(RyuseiEventMapping.FromTaskEvent(taskEventKind, _project, _task, "{}"));
    }

    [Fact]
    public void An_agent_message_maps_to_gc_message_with_its_author()
    {
        var mapped = GcMessage(_gc, "agent", _agent);

        Assert.Equal("gc.message", mapped.Kind);
        Assert.Equal(_gc, mapped.GcId);
        Assert.Equal(_agent, mapped.AuthorAgentId);
        Assert.Null(mapped.ProjectId);
        Assert.Contains("\"seq\":7", mapped.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public void An_owner_message_maps_to_gc_message_with_no_agent_author()
    {
        var mapped = GcMessage(_gc, "owner", _owner);

        Assert.Equal("gc.message", mapped.Kind);
        Assert.Null(mapped.AuthorAgentId);
    }

    [Theory]
    [InlineData("message_edit")]
    [InlineData("system")]
    [InlineData("structured")]
    [InlineData("council.open")]
    public void GC_events_other_than_a_new_message_or_a_council_decision_are_not_mapped(string gcEventKind)
    {
        Assert.Null(RyuseiEventMapping.FromGcEvent(gcEventKind, _gc, "agent", _agent, 3));
    }

    [Fact]
    public void A_council_decision_maps_to_council_decision_recorded_in_its_gc()
    {
        var mapped = RyuseiEventMapping.FromGcEvent("council.decision", _gc, "system", _owner, 9);

        Assert.NotNull(mapped);
        Assert.Equal("council.decision_recorded", mapped.Kind);
        Assert.Equal(_gc, mapped.GcId);
        Assert.True(RyuseiPredicates.TryParse(mapped.Kind, out _));
    }

    [Fact]
    public void A_mapped_envelope_matches_its_predicate_and_nothing_else()
    {
        var predicate = new RyuseiPredicate { Kind = "task.transitioned" };

        Assert.True(RyuseiPredicateMatcher.Matches(predicate, TaskEvent("task.transitioned").ToMatchEvent()));
        Assert.False(RyuseiPredicateMatcher.Matches(predicate, TaskEvent("task.blocked").ToMatchEvent()));
    }

    [Fact]
    public void A_gc_message_predicate_with_a_gc_filter_matches_only_that_gc()
    {
        var predicate = new RyuseiPredicate { Kind = "gc.message", GcId = _gc };

        Assert.True(RyuseiPredicateMatcher.Matches(predicate, GcMessage(_gc, "agent", _agent).ToMatchEvent()));
        Assert.False(RyuseiPredicateMatcher.Matches(predicate, GcMessage(_other, "agent", _agent).ToMatchEvent()));
    }

    // Scope: a GC-sourced event carries no project, so its predicate must name its GC.

    [Fact]
    public void A_task_event_is_in_scope_for_any_predicate()
    {
        Assert.True(RyuseiEventRules.IsInScope(new RyuseiPredicate { Kind = "task.transitioned" }, TaskEvent("task.transitioned")));
    }

    [Fact]
    public void A_gc_event_is_out_of_scope_for_a_predicate_that_names_no_gc()
    {
        var evt = GcMessage(_gc, "agent", _agent);

        Assert.False(RyuseiEventRules.IsInScope(new RyuseiPredicate { Kind = "gc.message" }, evt));
        Assert.True(RyuseiEventRules.IsInScope(new RyuseiPredicate { Kind = "gc.message", GcId = _gc }, evt));
    }

    // Debounce arithmetic.

    [Fact]
    public void A_debounce_deadline_is_now_plus_the_debounce_when_nothing_is_pending()
    {
        Assert.Equal(_now.AddSeconds(30), RyuseiDebounce.Deadline(_now, null, TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void A_pending_debounce_deadline_is_kept_so_a_burst_fires_once()
    {
        var pending = _now.AddSeconds(30);

        Assert.Equal(pending, RyuseiDebounce.Deadline(_now.AddSeconds(10), pending, TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void A_negative_debounce_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RyuseiDebounce.Deadline(_now, null, TimeSpan.FromSeconds(-1)));
    }

    // Watchers: a match arms the trigger; the tick fires it.

    [Fact]
    public void An_idle_watcher_match_arms_it_with_the_debounce_deadline()
    {
        var spec = new RyuseiWatcherTriggerSpec(new RyuseiPredicate { Kind = "task.transitioned" }, 60, 30, null, null);

        var match = RyuseiEventRules.MatchWatcher(Row(spec), spec, _now);

        Assert.Equal(RyuseiTriggerState.Active, match.NextState);
        Assert.Equal(_now.AddSeconds(30), match.NextFireAt);
        Assert.True(match.Changed);
    }

    [Fact]
    public void A_match_on_an_already_armed_watcher_is_coalesced()
    {
        var spec = new RyuseiWatcherTriggerSpec(new RyuseiPredicate { Kind = "task.transitioned" }, 60, 30, null, null);
        var deadline = _now.AddSeconds(20);

        var match = RyuseiEventRules.MatchWatcher(Row(spec, deadline), spec, _now.AddSeconds(10));

        Assert.False(match.Changed);
        Assert.Equal(deadline, match.NextFireAt);
    }

    [Fact]
    public void A_watcher_match_after_its_expiry_expires_it_instead_of_arming_it()
    {
        var spec = new RyuseiWatcherTriggerSpec(new RyuseiPredicate { Kind = "task.transitioned" }, 60, 30, _now, null);

        var match = RyuseiEventRules.MatchWatcher(Row(spec, expiresAt: _now), spec, _now.AddSeconds(1));

        Assert.Equal(RyuseiTriggerState.Expired, match.NextState);
        Assert.Null(match.NextFireAt);
        Assert.True(match.Changed);
    }

    // Event triggers: fire once on the first match.

    [Fact]
    public void The_first_match_wakes_the_target_and_moves_the_event_to_fired()
    {
        var spec = new RyuseiEventTriggerSpec(new RyuseiPredicate { Kind = "task.transitioned" });

        var decision = RyuseiEventRules.DecideEventMatch(
            Row(spec),
            [new RyuseiFireTarget(_agent, RyuseiFireTargetStatus.Wakeable)],
            _now,
            "task.transitioned");

        Assert.Equal(RyuseiTriggerState.Fired, decision.NextState);
        Assert.NotNull(decision.Fire);
        Assert.Equal(RyuseiFireOutcome.Woke, decision.Fire.Outcome);
        Assert.Equal(RyuseiTriggerState.Fired, decision.Fire.NextState);
        Assert.Equal(_now, decision.Fire.ScheduledFor);
        Assert.Equal("ryusei_event", decision.Fire.WakeReason);
        Assert.Single(decision.Fire.WakeAgentIds);
        Assert.Equal(_agent, decision.Fire.WakeAgentIds[0]);
    }

    [Fact]
    public void An_event_wakes_only_its_wakeable_targets()
    {
        var spec = new RyuseiEventTriggerSpec(new RyuseiPredicate { Kind = "task.transitioned" });

        var decision = RyuseiEventRules.DecideEventMatch(
            Row(spec, targets: [_agent, _other]),
            [
                new RyuseiFireTarget(_agent, RyuseiFireTargetStatus.Wakeable),
                new RyuseiFireTarget(_other, RyuseiFireTargetStatus.Resting),
            ],
            _now,
            "task.transitioned");

        Assert.NotNull(decision.Fire);
        Assert.Equal(RyuseiFireOutcome.Woke, decision.Fire.Outcome);
        Assert.Single(decision.Fire.WakeAgentIds);
        Assert.Equal(_agent, decision.Fire.WakeAgentIds[0]);
    }

    [Fact]
    public void An_event_with_only_resting_targets_is_skipped_and_still_fires_once()
    {
        var spec = new RyuseiEventTriggerSpec(new RyuseiPredicate { Kind = "task.transitioned" });

        var decision = RyuseiEventRules.DecideEventMatch(
            Row(spec),
            [new RyuseiFireTarget(_agent, RyuseiFireTargetStatus.Resting)],
            _now,
            "task.transitioned");

        Assert.Equal(RyuseiTriggerState.Fired, decision.NextState);
        Assert.NotNull(decision.Fire);
        Assert.Equal(RyuseiFireOutcome.SkippedResting, decision.Fire.Outcome);
        Assert.Empty(decision.Fire.WakeAgentIds);
        Assert.Null(decision.Fire.WakeReason);
    }

    [Fact]
    public void An_event_whose_targets_are_all_retired_or_missing_expires()
    {
        var spec = new RyuseiEventTriggerSpec(new RyuseiPredicate { Kind = "task.transitioned" });

        var decision = RyuseiEventRules.DecideEventMatch(
            Row(spec, targets: [_agent, _other]),
            [
                new RyuseiFireTarget(_agent, RyuseiFireTargetStatus.Retired),
                new RyuseiFireTarget(_other, RyuseiFireTargetStatus.Missing),
            ],
            _now,
            "task.transitioned");

        Assert.Equal(RyuseiTriggerState.Expired, decision.NextState);
        Assert.NotNull(decision.Fire);
        Assert.Equal(RyuseiFireOutcome.SkippedResting, decision.Fire.Outcome);
        Assert.Equal(RyuseiTriggerState.Expired, decision.Fire.NextState);
    }

    [Fact]
    public void An_event_past_its_expiry_expires_without_a_fire_row()
    {
        var spec = new RyuseiEventTriggerSpec(new RyuseiPredicate { Kind = "task.transitioned" });

        var decision = RyuseiEventRules.DecideEventMatch(
            Row(spec, expiresAt: _now),
            [new RyuseiFireTarget(_agent, RyuseiFireTargetStatus.Wakeable)],
            _now.AddSeconds(1),
            "task.transitioned");

        Assert.Equal(RyuseiTriggerState.Expired, decision.NextState);
        Assert.Null(decision.Fire);
    }
}
