using Bazaar.Server.Presence.Abstractions;
using Bazaar.Server.WorkLife;

namespace Bazaar.Server.Presence.Tests;

/// <summary>The activity projector (docs/design/23): every presence path, ordering, revisions and the unknown rule.</summary>
public class ActivityProjectorTests
{
    private static readonly Guid _gc = Guid.Parse("00000000-0000-7000-8000-0000000000c1");
    private static readonly Guid _turn = Guid.Parse("00000000-0000-7000-8000-0000000000d1");

    public static TheoryData<PresenceAvailability, PresenceActivityKind, PresenceToolClass, PresenceGcContext, string> Paths => new()
    {
        { PresenceAvailability.Vacation, PresenceActivityKind.ModelCall, PresenceToolClass.None, PresenceGcContext.Other, "on vacation" },
        { PresenceAvailability.Vacation, PresenceActivityKind.Idle, PresenceToolClass.None, PresenceGcContext.Other, "on vacation" },
        { PresenceAvailability.Off, PresenceActivityKind.Idle, PresenceToolClass.None, PresenceGcContext.Other, "sleeping" },
        { PresenceAvailability.Off, PresenceActivityKind.Drafting, PresenceToolClass.None, PresenceGcContext.Other, "sleeping" },
        { PresenceAvailability.Break, PresenceActivityKind.Idle, PresenceToolClass.None, PresenceGcContext.Other, "sleeping" },
        { PresenceAvailability.Break, PresenceActivityKind.ModelCall, PresenceToolClass.None, PresenceGcContext.Other, "thinking" },
        { PresenceAvailability.OnShift, PresenceActivityKind.ModelCall, PresenceToolClass.None, PresenceGcContext.CouncilOrVote, "in meeting" },
        { PresenceAvailability.Draining, PresenceActivityKind.Tool, PresenceToolClass.Research, PresenceGcContext.Other, "researching" },
        { PresenceAvailability.OnShift, PresenceActivityKind.Tool, PresenceToolClass.Review, PresenceGcContext.ProjectGc, "reviewing" },
        { PresenceAvailability.OnShift, PresenceActivityKind.ModelCall, PresenceToolClass.None, PresenceGcContext.ProjectGc, "thinking" },
        { PresenceAvailability.OnShift, PresenceActivityKind.Tool, PresenceToolClass.EditOrBuild, PresenceGcContext.ProjectGc, "working" },
        { PresenceAvailability.OnShift, PresenceActivityKind.Tool, PresenceToolClass.None, PresenceGcContext.OtherProjectGc, "helping another project" },
        { PresenceAvailability.OnShift, PresenceActivityKind.Idle, PresenceToolClass.None, PresenceGcContext.Other, "working" },
        { PresenceAvailability.Draining, PresenceActivityKind.Drafting, PresenceToolClass.None, PresenceGcContext.Other, "working" },
        { PresenceAvailability.OnShift, PresenceActivityKind.Waiting, PresenceToolClass.None, PresenceGcContext.Other, "working" },
    };

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task Every_availability_and_activity_path_matches_the_presence_rules(
        PresenceAvailability availability,
        PresenceActivityKind activity,
        PresenceToolClass tool,
        PresenceGcContext gc,
        string expected)
    {
        var rig = new PresenceRig();
        await rig.Send(ActivityEvent.AvailabilityChanged(PresenceRig.Ada, PresenceRig.At(1), availability));
        await rig.Send(ActivityEvent.TurnChanged(PresenceRig.Ada, PresenceRig.At(2), activity, tool, gc, _gc, _turn));

        var row = await rig.RowAsync(PresenceRig.Ada);
        Assert.Equal(expected, row.Presence);
        Assert.Equal(availability, row.Availability);
        Assert.Equal(activity, row.ActivityKind);
        Assert.Equal(2, row.Revision);
    }

    [Fact]
    public async Task Idle_label_setting_idle_shows_idle_without_the_idle_overlay()
    {
        var rig = new PresenceRig(new PresenceOptions { IdleLabel = "idle" });
        await rig.Send(ActivityEvent.AvailabilityChanged(PresenceRig.Ada, PresenceRig.At(1), PresenceAvailability.OnShift));

        var row = await rig.RowAsync(PresenceRig.Ada);
        Assert.Equal("idle", row.Presence);
        Assert.Equal(PresenceOverlay.None, row.Overlay);
    }

    [Fact]
    public async Task Default_idle_label_working_adds_the_idle_overlay()
    {
        var rig = new PresenceRig();
        await rig.Send(ActivityEvent.AvailabilityChanged(PresenceRig.Ada, PresenceRig.At(1), PresenceAvailability.OnShift));

        Assert.Equal(PresenceOverlay.Idle, (await rig.RowAsync(PresenceRig.Ada)).Overlay);
    }

    [Fact]
    public async Task Flag_events_set_and_clear_their_overlays()
    {
        var rig = new PresenceRig(new PresenceOptions { IdleLabel = "idle" });
        await rig.Send(ActivityEvent.AvailabilityChanged(PresenceRig.Ada, PresenceRig.At(1), PresenceAvailability.OnShift));
        await rig.Send(ActivityEvent.Flag(PresenceRig.Ada, PresenceRig.At(2), ActivityEventKind.QuotaThrottle, true));
        await rig.Send(ActivityEvent.Flag(PresenceRig.Ada, PresenceRig.At(3), ActivityEventKind.Covered, true));
        await rig.Send(ActivityEvent.Flag(PresenceRig.Ada, PresenceRig.At(4), ActivityEventKind.Ward, true));

        Assert.Equal(PresenceOverlay.Throttled | PresenceOverlay.Covered | PresenceOverlay.Ward, (await rig.RowAsync(PresenceRig.Ada)).Overlay);

        await rig.Send(ActivityEvent.Flag(PresenceRig.Ada, PresenceRig.At(5), ActivityEventKind.QuotaThrottle, false));
        await rig.Send(ActivityEvent.Flag(PresenceRig.Ada, PresenceRig.At(6), ActivityEventKind.Ward, false));

        Assert.Equal(PresenceOverlay.Covered, (await rig.RowAsync(PresenceRig.Ada)).Overlay);
    }

    [Fact]
    public async Task Break_availability_shows_the_on_break_overlay()
    {
        var rig = new PresenceRig();
        await rig.Send(ActivityEvent.AvailabilityChanged(PresenceRig.Ada, PresenceRig.At(1), PresenceAvailability.Break));

        var row = await rig.RowAsync(PresenceRig.Ada);
        Assert.Equal("sleeping", row.Presence);
        Assert.Equal(PresenceOverlay.OnBreak, row.Overlay);
    }

    [Fact]
    public async Task Approval_wait_shows_the_waiting_overlay_and_ends_idle()
    {
        var rig = new PresenceRig(new PresenceOptions { IdleLabel = "idle" });
        await rig.Send(ActivityEvent.AvailabilityChanged(PresenceRig.Ada, PresenceRig.At(1), PresenceAvailability.OnShift));
        await rig.Send(ActivityEvent.Flag(PresenceRig.Ada, PresenceRig.At(2), ActivityEventKind.ApprovalWait, true));

        var waiting = await rig.RowAsync(PresenceRig.Ada);
        Assert.Equal(PresenceActivityKind.Waiting, waiting.ActivityKind);
        Assert.Equal(PresenceOverlay.WaitingForApproval, waiting.Overlay);

        await rig.Send(ActivityEvent.Flag(PresenceRig.Ada, PresenceRig.At(3), ActivityEventKind.ApprovalWait, false));

        var after = await rig.RowAsync(PresenceRig.Ada);
        Assert.Equal(PresenceActivityKind.Idle, after.ActivityKind);
        Assert.Equal(PresenceOverlay.None, after.Overlay);
    }

    [Fact]
    public async Task Kanban_wait_uses_the_idle_rule_with_no_approval_overlay()
    {
        var rig = new PresenceRig(new PresenceOptions { IdleLabel = "idle" });
        await rig.Send(ActivityEvent.AvailabilityChanged(PresenceRig.Ada, PresenceRig.At(1), PresenceAvailability.OnShift));
        await rig.Send(ActivityEvent.Flag(PresenceRig.Ada, PresenceRig.At(2), ActivityEventKind.KanbanWait, true));

        var row = await rig.RowAsync(PresenceRig.Ada);
        Assert.Equal(PresenceActivityKind.Waiting, row.ActivityKind);
        Assert.Equal("idle", row.Presence);
        Assert.Equal(PresenceOverlay.None, row.Overlay);
    }

    [Fact]
    public async Task Turn_end_clears_the_gc_and_turn_ids_and_the_label()
    {
        var rig = new PresenceRig();
        await rig.Send(ActivityEvent.AvailabilityChanged(PresenceRig.Ada, PresenceRig.At(1), PresenceAvailability.OnShift));
        await rig.Send(ActivityEvent.TurnChanged(PresenceRig.Ada, PresenceRig.At(2), PresenceActivityKind.ModelCall, gcId: _gc, turnId: _turn, label: "planning"));

        var during = await rig.RowAsync(PresenceRig.Ada);
        Assert.Equal((_gc, _turn, "planning"), (during.GcId, during.TurnId, during.Label));

        await rig.Send(ActivityEvent.TurnChanged(PresenceRig.Ada, PresenceRig.At(3), PresenceActivityKind.Idle));

        var after = await rig.RowAsync(PresenceRig.Ada);
        Assert.Equal((null, null, null), (after.GcId, after.TurnId, after.Label));
    }

    [Fact]
    public async Task Events_older_than_the_last_applied_source_time_are_dropped()
    {
        var rig = new PresenceRig();
        await rig.Send(ActivityEvent.AvailabilityChanged(PresenceRig.Ada, PresenceRig.At(10), PresenceAvailability.OnShift));
        await rig.Send(ActivityEvent.TurnChanged(PresenceRig.Ada, PresenceRig.At(20), PresenceActivityKind.ModelCall));
        await rig.Send(ActivityEvent.TurnChanged(PresenceRig.Ada, PresenceRig.At(15), PresenceActivityKind.Idle));

        var row = await rig.RowAsync(PresenceRig.Ada);
        Assert.Equal(2, row.Revision);
        Assert.Equal("thinking", row.Presence);
        Assert.Equal(2, rig.Publisher.Published.Count);
    }

    [Fact]
    public async Task A_duplicate_event_changes_nothing()
    {
        var rig = new PresenceRig();
        var e = ActivityEvent.AvailabilityChanged(PresenceRig.Ada, PresenceRig.At(1), PresenceAvailability.OnShift);
        await rig.Send(e);
        await rig.Send(e with { });

        Assert.Equal(1, (await rig.RowAsync(PresenceRig.Ada)).Revision);
        Assert.Single(rig.Publisher.Published);
    }

    [Fact]
    public async Task Two_different_events_with_the_same_source_time_both_apply()
    {
        var rig = new PresenceRig();
        await rig.Send(ActivityEvent.AvailabilityChanged(PresenceRig.Ada, PresenceRig.At(1), PresenceAvailability.OnShift));
        await rig.Send(ActivityEvent.Flag(PresenceRig.Ada, PresenceRig.At(1), ActivityEventKind.Covered, true));

        var row = await rig.RowAsync(PresenceRig.Ada);
        Assert.Equal(2, row.Revision);
        Assert.True(row.Overlay.HasFlag(PresenceOverlay.Covered));
    }

    [Fact]
    public async Task Revisions_are_monotonic_per_agent_and_each_delta_carries_agent_and_revision()
    {
        var rig = new PresenceRig();
        await rig.Send(ActivityEvent.AvailabilityChanged(PresenceRig.Ada, PresenceRig.At(1), PresenceAvailability.OnShift));
        await rig.Send(ActivityEvent.AvailabilityChanged(PresenceRig.Bob, PresenceRig.At(1), PresenceAvailability.Off));
        await rig.Send(ActivityEvent.TurnChanged(PresenceRig.Ada, PresenceRig.At(2), PresenceActivityKind.ModelCall));
        await rig.Send(ActivityEvent.TurnChanged(PresenceRig.Ada, PresenceRig.At(3), PresenceActivityKind.Idle));

        Assert.Equal([1L, 2L, 3L], rig.Publisher.Published.Where(r => r.AgentId == PresenceRig.Ada).Select(r => r.Revision));
        Assert.Equal([1L], rig.Publisher.Published.Where(r => r.AgentId == PresenceRig.Bob).Select(r => r.Revision));
        Assert.Equal(3, (await rig.RowAsync(PresenceRig.Ada)).Revision);
    }

    [Fact]
    public async Task Concurrent_events_for_one_agent_are_serialized_into_unique_revisions()
    {
        var rig = new PresenceRig();
        const int count = 60;
        var at = PresenceRig.At(5);
        var tasks = Enumerable.Range(0, count)
            .Select(i => Task.Run(() => rig.Send(ActivityEvent.TurnChanged(PresenceRig.Ada, at, PresenceActivityKind.ModelCall, turnId: Guid.NewGuid())).AsTask()))
            .ToArray();
        await Task.WhenAll(tasks);

        Assert.Equal(count, (await rig.RowAsync(PresenceRig.Ada)).Revision);
        Assert.Equal(Enumerable.Range(1, count).Select(i => (long)i), rig.Publisher.Published.Select(r => r.Revision).Order());
    }

    [Fact]
    public async Task Labels_are_cut_to_80_characters_and_lose_control_characters()
    {
        var rig = new PresenceRig();
        await rig.Send(ActivityEvent.TurnChanged(PresenceRig.Ada, PresenceRig.At(1), PresenceActivityKind.ModelCall, label: new string('x', 120)));
        Assert.Equal(80, (await rig.RowAsync(PresenceRig.Ada)).Label!.Length);

        await rig.Send(ActivityEvent.TurnChanged(PresenceRig.Ada, PresenceRig.At(2), PresenceActivityKind.ModelCall, label: "a\nb\tc"));
        Assert.Equal("abc", (await rig.RowAsync(PresenceRig.Ada)).Label);
    }

    [Fact]
    public async Task Kinds_only_mode_drops_the_label()
    {
        var rig = new PresenceRig(new PresenceOptions { ActivityLabelMode = ActivityLabelMode.KindsOnly });
        await rig.Send(ActivityEvent.TurnChanged(PresenceRig.Ada, PresenceRig.At(1), PresenceActivityKind.ModelCall, label: "secret plan"));

        Assert.Null((await rig.RowAsync(PresenceRig.Ada)).Label);
    }

    [Fact]
    public async Task A_publisher_failure_does_not_lose_the_stored_row()
    {
        var rig = new PresenceRig();
        rig.Publisher.Throw = true;
        await rig.Send(ActivityEvent.AvailabilityChanged(PresenceRig.Ada, PresenceRig.At(1), PresenceAvailability.OnShift));

        Assert.Equal(1, (await rig.RowAsync(PresenceRig.Ada)).Revision);
    }

    [Fact]
    public async Task Since_moves_only_when_the_label_or_activity_changes()
    {
        var rig = new PresenceRig();
        await rig.Send(ActivityEvent.TurnChanged(PresenceRig.Ada, PresenceRig.At(1), PresenceActivityKind.ModelCall));
        await rig.Send(ActivityEvent.Flag(PresenceRig.Ada, PresenceRig.At(9), ActivityEventKind.QuotaThrottle, true));

        Assert.Equal(PresenceRig.At(1), (await rig.RowAsync(PresenceRig.Ada)).Since);
    }

    [Fact]
    public async Task A_rebuilt_on_shift_row_with_no_turn_event_shows_unknown_after_ten_minutes()
    {
        var rig = new PresenceRig();
        await rig.Projector.RebuildAsync([new PresenceSeed(PresenceRig.Ada, PresenceAvailability.OnShift)], TestContext.Current.CancellationToken);
        Assert.Equal("working", (await rig.RowAsync(PresenceRig.Ada)).Presence);

        rig.Time.Advance(TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(59));
        Assert.Equal(0, await rig.Projector.SweepAsync(TestContext.Current.CancellationToken));

        rig.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, await rig.Projector.SweepAsync(TestContext.Current.CancellationToken));

        var row = await rig.RowAsync(PresenceRig.Ada);
        Assert.Equal(PresenceRow.UnknownLabel, row.Presence);
        Assert.Equal(2, row.Revision);
        Assert.Equal(PresenceRow.UnknownLabel, rig.Publisher.Published[^1].Presence);

        // A second sweep does not bump the revision again.
        Assert.Equal(0, await rig.Projector.SweepAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_turn_event_clears_unknown_and_restarts_the_clock()
    {
        var rig = new PresenceRig();
        await rig.Projector.RebuildAsync([new PresenceSeed(PresenceRig.Ada, PresenceAvailability.OnShift)], TestContext.Current.CancellationToken);
        rig.Time.Advance(TimeSpan.FromMinutes(10));
        await rig.Projector.SweepAsync(TestContext.Current.CancellationToken);

        await rig.Send(ActivityEvent.TurnChanged(PresenceRig.Ada, PresenceRig.At(700), PresenceActivityKind.ModelCall));
        Assert.Equal("thinking", (await rig.RowAsync(PresenceRig.Ada)).Presence);

        rig.Time.Advance(TimeSpan.FromMinutes(9));
        Assert.Equal(0, await rig.Projector.SweepAsync(TestContext.Current.CancellationToken));
        rig.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, await rig.Projector.SweepAsync(TestContext.Current.CancellationToken));
        Assert.Equal(PresenceRow.UnknownLabel, (await rig.RowAsync(PresenceRig.Ada)).Presence);
    }

    [Fact]
    public async Task A_rebuilt_off_shift_row_never_goes_unknown()
    {
        var rig = new PresenceRig();
        await rig.Projector.RebuildAsync(
            [new PresenceSeed(PresenceRig.Ada, PresenceAvailability.Off), new PresenceSeed(PresenceRig.Bob, PresenceAvailability.Vacation)],
            TestContext.Current.CancellationToken);
        rig.Time.Advance(TimeSpan.FromHours(3));

        Assert.Equal(0, await rig.Projector.SweepAsync(TestContext.Current.CancellationToken));
        Assert.Equal("sleeping", (await rig.RowAsync(PresenceRig.Ada)).Presence);
        Assert.Equal("on vacation", (await rig.RowAsync(PresenceRig.Bob)).Presence);
    }

    [Fact]
    public async Task A_rebuilt_open_turn_shows_its_activity_then_goes_unknown_without_turn_events()
    {
        var rig = new PresenceRig();
        var open = ActivityEvent.TurnChanged(PresenceRig.Ada, PresenceRig.At(-30), PresenceActivityKind.Tool, PresenceToolClass.Research, gcId: _gc, turnId: _turn);
        await rig.Projector.RebuildAsync([new PresenceSeed(PresenceRig.Ada, PresenceAvailability.OnShift, OpenTurn: open)], TestContext.Current.CancellationToken);

        var row = await rig.RowAsync(PresenceRig.Ada);
        Assert.Equal("researching", row.Presence);
        Assert.Equal(_turn, row.TurnId);

        rig.Time.Advance(TimeSpan.FromMinutes(10));
        await rig.Projector.SweepAsync(TestContext.Current.CancellationToken);
        Assert.Equal(PresenceRow.UnknownLabel, (await rig.RowAsync(PresenceRig.Ada)).Presence);
    }

    [Fact]
    public async Task A_live_turn_that_goes_quiet_shows_unknown_but_an_idle_one_does_not()
    {
        var rig = new PresenceRig();
        await rig.Send(ActivityEvent.AvailabilityChanged(PresenceRig.Ada, PresenceRig.At(1), PresenceAvailability.OnShift));
        await rig.Send(ActivityEvent.TurnChanged(PresenceRig.Ada, PresenceRig.At(2), PresenceActivityKind.ModelCall));
        await rig.Send(ActivityEvent.AvailabilityChanged(PresenceRig.Bob, PresenceRig.At(1), PresenceAvailability.OnShift));

        rig.Time.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(1, await rig.Projector.SweepAsync(TestContext.Current.CancellationToken));

        Assert.Equal(PresenceRow.UnknownLabel, (await rig.RowAsync(PresenceRig.Ada)).Presence);
        Assert.Equal("working", (await rig.RowAsync(PresenceRig.Bob)).Presence);
    }
}
