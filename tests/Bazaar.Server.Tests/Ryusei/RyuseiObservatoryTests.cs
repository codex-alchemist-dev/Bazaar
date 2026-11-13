using System.Text.Json.Nodes;
using Bazaar.RyuseiFuda.Orrery;

namespace Bazaar.Server.Tests.Ryusei;

/// <summary>
/// The RyuseiFuda Observatory events as pure functions (doc 38, Observability; doc 23). No database: the committed path is covered
/// by <see cref="RyuseiObservatoryDatabaseTests"/>.
/// </summary>
public sealed class RyuseiObservatoryTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid _fire = Guid.Parse("f2f2f2f2-f2f2-7f2f-8f2f-f2f2f2f2f2f2");
    private static readonly Guid _trigger = Guid.Parse("11111111-1111-7111-8111-111111111111");
    private static readonly Guid _project = Guid.Parse("22222222-2222-7222-8222-222222222222");
    private static readonly Guid _creator = Guid.Parse("33333333-3333-7333-8333-333333333333");
    private static readonly Guid _target = Guid.Parse("44444444-4444-7444-8444-444444444444");
    private const string _secretPurpose = "watch the release-candidate tag for the customer escalation";

    private static RyuseiTriggerRecord Trigger(RyuseiTriggerState state = RyuseiTriggerState.Active) => new(
        TriggerId: _trigger,
        ProjectId: _project,
        CreatedBy: _creator,
        Kind: RyuseiTriggerKind.Interval,
        TargetAgentIds: [_target],
        Purpose: _secretPurpose,
        SpecJson: "{\"every\":\"PT30M\"}",
        TaskId: null,
        AttachGcId: null,
        PingMode: RyuseiPingMode.None,
        State: state,
        NextFireAt: _now,
        FireCount: 2,
        ConsecutiveRefusals: 2,
        MaxFires: null,
        ExpiresAt: null,
        ChainDepth: 0,
        Version: 3,
        CreatedAt: _now,
        UpdatedAt: _now);

    private static RyuseiFireDecision Decision(
        RyuseiFireOutcome outcome,
        RyuseiTriggerState next,
        int consecutive = 0,
        IReadOnlyList<Guid>? wake = null) => new(
        outcome,
        ScheduledFor: _now,
        NextState: next,
        NextFireAt: _now.AddMinutes(30),
        ConsecutiveRefusals: consecutive,
        WakeAgentIds: wake ?? [],
        WakeReason: wake is null ? null : "ryusei_interval",
        Cause: new JsonObject());

    [Fact]
    public void Event_names_match_doc_23()
    {
        Assert.Equal("ryusei.trigger_created", RyuseiObservatoryEvents.TriggerCreated);
        Assert.Equal("ryusei.trigger_paused", RyuseiObservatoryEvents.TriggerPaused);
        Assert.Equal("ryusei.trigger_resumed", RyuseiObservatoryEvents.TriggerResumed);
        Assert.Equal("ryusei.trigger_cancelled", RyuseiObservatoryEvents.TriggerCancelled);
        Assert.Equal("ryusei.fire", RyuseiObservatoryEvents.Fire);
        Assert.Equal("ryusei.auto_paused", RyuseiObservatoryEvents.AutoPaused);
    }

    [Theory]
    [InlineData(RyuseiTriggerState.Paused, "ryusei.trigger_paused")]
    [InlineData(RyuseiTriggerState.Active, "ryusei.trigger_resumed")]
    [InlineData(RyuseiTriggerState.Cancelled, "ryusei.trigger_cancelled")]
    public void State_change_names_follow_the_new_state(RyuseiTriggerState to, string expected) =>
        Assert.Equal(expected, RyuseiObservatoryEvents.NameForStateChange(to));

    [Fact]
    public void Created_fields_carry_ids_and_a_purpose_hash_never_the_text()
    {
        var fields = RyuseiObservatoryEvents.CreatedFields(_trigger, _project, RyuseiTriggerKind.Watcher, [_target], RyuseiPingMode.None, 1, _secretPurpose);

        Assert.Equal(_trigger.ToString("D"), fields["triggerId"]!.GetValue<string>());
        Assert.Equal(_project.ToString("D"), fields["projectId"]!.GetValue<string>());
        Assert.Equal("watcher", fields["kind"]!.GetValue<string>());
        Assert.Equal(RyuseiObservatoryEvents.Hash(_secretPurpose), fields["purposeHash"]!.GetValue<string>());
        Assert.Equal(64, fields["purposeHash"]!.GetValue<string>().Length);
        Assert.DoesNotContain(_secretPurpose, fields.ToJsonString());
    }

    [Fact]
    public void Hash_is_stable_lowercase_sha256()
    {
        // SHA-256 of the empty string.
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", RyuseiObservatoryEvents.Hash(string.Empty));
        Assert.Equal(RyuseiObservatoryEvents.Hash("a"), RyuseiObservatoryEvents.Hash("a"));
        Assert.NotEqual(RyuseiObservatoryEvents.Hash("a"), RyuseiObservatoryEvents.Hash("b"));
    }

    [Fact]
    public void Created_event_copies_the_fields_it_is_given()
    {
        var fields = RyuseiObservatoryEvents.CreatedFields(_trigger, _project, RyuseiTriggerKind.Timer, [_target], RyuseiPingMode.None, 0, "p");
        var evt = RyuseiObservatoryEvents.Created(fields, _now);

        Assert.Equal("ryusei.trigger_created", evt.Name);
        Assert.Equal(_now, evt.At);
        Assert.Equal(fields.ToJsonString(), evt.Fields.ToJsonString());
        Assert.NotSame(fields, evt.Fields);
    }

    [Fact]
    public void State_change_fields_name_both_states()
    {
        var fields = RyuseiObservatoryEvents.StateChangedFields(_trigger, _project, RyuseiTriggerState.Active, RyuseiTriggerState.Paused);

        Assert.Equal("active", fields["fromState"]!.GetValue<string>());
        Assert.Equal("paused", fields["toState"]!.GetValue<string>());
    }

    [Fact]
    public void A_woke_fire_emits_one_fire_event_and_no_auto_pause()
    {
        var events = RyuseiObservatoryEvents.ForFire(_fire, Trigger(), Decision(RyuseiFireOutcome.Woke, RyuseiTriggerState.Active, wake: [_target]), _now);

        var evt = Assert.Single(events);
        Assert.Equal("ryusei.fire", evt.Name);
        Assert.Equal("woke", evt.Fields["outcome"]!.GetValue<string>());
        Assert.Equal(1, evt.Fields["wokenCount"]!.GetValue<int>());
        Assert.Equal(_fire.ToString("D"), evt.Fields["fireId"]!.GetValue<string>());
    }

    [Fact]
    public void A_skipped_fire_emits_a_fire_event_with_the_skip_outcome()
    {
        var events = RyuseiObservatoryEvents.ForFire(_fire, Trigger(), Decision(RyuseiFireOutcome.SkippedResting, RyuseiTriggerState.Active), _now);

        var evt = Assert.Single(events);
        Assert.Equal("skipped_resting", evt.Fields["outcome"]!.GetValue<string>());
        Assert.Equal(0, evt.Fields["wokenCount"]!.GetValue<int>());
    }

    [Fact]
    public void A_refusal_that_pauses_the_trigger_adds_an_auto_paused_event()
    {
        var decision = Decision(RyuseiFireOutcome.RefusedCap, RyuseiTriggerState.Paused, consecutive: 3);
        var events = RyuseiObservatoryEvents.ForFire(_fire, Trigger(), decision, _now);

        Assert.Equal(["ryusei.fire", "ryusei.auto_paused"], events.Select(e => e.Name).ToArray());
        var paused = events[1];
        Assert.Equal("refused_cap", paused.Fields["outcome"]!.GetValue<string>());
        Assert.Equal(3, paused.Fields["consecutiveRefusals"]!.GetValue<int>());
        Assert.Equal(_fire.ToString("D"), paused.Fields["fireId"]!.GetValue<string>());
    }

    [Fact]
    public void A_refusal_that_keeps_the_trigger_active_adds_no_auto_paused_event()
    {
        var events = RyuseiObservatoryEvents.ForFire(_fire, Trigger(), Decision(RyuseiFireOutcome.RefusedCooldown, RyuseiTriggerState.Active, consecutive: 1), _now);

        Assert.Equal(["ryusei.fire"], events.Select(e => e.Name).ToArray());
    }

    [Fact]
    public void A_trigger_already_paused_does_not_auto_pause_again()
    {
        var events = RyuseiObservatoryEvents.ForFire(_fire, Trigger(RyuseiTriggerState.Paused), Decision(RyuseiFireOutcome.Error, RyuseiTriggerState.Paused), _now);

        Assert.Equal(["ryusei.fire"], events.Select(e => e.Name).ToArray());
    }

    [Fact]
    public void A_malformed_fire_that_pauses_an_active_trigger_is_an_auto_pause()
    {
        var events = RyuseiObservatoryEvents.ForFire(_fire, Trigger(), Decision(RyuseiFireOutcome.Error, RyuseiTriggerState.Paused), _now);

        Assert.Equal(["ryusei.fire", "ryusei.auto_paused"], events.Select(e => e.Name).ToArray());
        Assert.Equal("error", events[1].Fields["outcome"]!.GetValue<string>());
    }

    [Fact]
    public void No_fire_event_carries_the_purpose_text()
    {
        var all = new List<RyuseiObservatoryEvent>();
        all.AddRange(RyuseiObservatoryEvents.ForFire(_fire, Trigger(), Decision(RyuseiFireOutcome.RefusedCap, RyuseiTriggerState.Paused, 3), _now));
        all.AddRange(RyuseiObservatoryEvents.ForFire(_fire, Trigger(), Decision(RyuseiFireOutcome.Woke, RyuseiTriggerState.Active, wake: [_target]), _now));

        foreach (var evt in all)
        {
            Assert.DoesNotContain(_secretPurpose, evt.Fields.ToJsonString());
        }
    }

    [Fact]
    public async Task Emit_sends_events_in_order_to_the_sink()
    {
        var sink = new RecordingSink();
        var events = new[]
        {
            new RyuseiObservatoryEvent("first", _now, new JsonObject()),
            new RyuseiObservatoryEvent("second", _now, new JsonObject()),
        };

        await RyuseiObservatoryEvents.EmitAsync(sink, events, TestContext.Current.CancellationToken);

        Assert.Equal(["first", "second"], sink.Names);
    }

    [Fact]
    public async Task Null_sink_accepts_every_event_and_keeps_nothing()
    {
        var evt = new RyuseiObservatoryEvent("ryusei.fire", _now, new JsonObject());

        // Completing without throwing is the whole contract: there is nothing to read back.
        await NullRyuseiObservatory.Instance.EmitAsync(evt, TestContext.Current.CancellationToken);
        await RyuseiObservatoryEvents.EmitAsync(NullRyuseiObservatory.Instance, [evt], TestContext.Current.CancellationToken);
    }

    private sealed class RecordingSink : IRyuseiObservatory
    {
        public List<string> Names { get; } = [];

        public ValueTask EmitAsync(RyuseiObservatoryEvent evt, CancellationToken ct = default)
        {
            Names.Add(evt.Name);
            return ValueTask.CompletedTask;
        }
    }
}
