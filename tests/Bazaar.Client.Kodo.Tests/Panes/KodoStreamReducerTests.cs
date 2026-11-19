using Bazaar.Contracts.Kodo;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Client.Kodo.Tests.Panes;

public class KodoStreamReducerTests
{
    private static readonly DateTimeOffset _start = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    internal static KodoStatusDto Status(string state = "healthy", string? underlying = null, int score = 90, params KodoControlDto[] controls) =>
        new(state, underlying, score, _start, controls);

    internal static KodoStreamEventDto Snapshot(long revision, string state = "healthy", string? underlying = null, IReadOnlyList<KodoComponentDto>? components = null, params KodoControlDto[] controls) =>
        new(revision, KodoStreamEventDto.SnapshotType, _start, null,
            new KodoStatusSnapshotDto(KodoStatusSnapshotDto.CurrentSchemaVersion, revision, Status(state, underlying, 90, controls), null, components ?? []));

    internal static KodoStreamEventDto Change(long revision, string previous, string state, string? underlying = null) =>
        new(revision, KodoStreamEventDto.StateChangedType, _start, new KodoStateChangeDto(previous, state, underlying), null);

    internal static KodoStreamEventDto Beat(long revision) => new(revision, KodoStreamEventDto.HeartbeatType, _start, null, null);

    private sealed class SettableTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static (KodoStreamReducer Reducer, FakeTimeProvider Time) Make()
    {
        var time = new FakeTimeProvider(_start);
        return (new KodoStreamReducer(time), time);
    }

    [Fact]
    public void Before_any_snapshot_the_display_is_unobserved_and_a_snapshot_is_needed()
    {
        var (reducer, _) = Make();

        Assert.Equal(KodoDisplayState.Unobserved, reducer.Display());
        Assert.True(reducer.NeedsSnapshot);
        Assert.Null(reducer.Revision);
    }

    [Fact]
    public void A_snapshot_then_in_order_deltas_apply()
    {
        var (reducer, _) = Make();

        Assert.Equal(KodoReduceResult.Applied, reducer.Apply(Snapshot(5)));
        Assert.Equal(KodoReduceResult.Applied, reducer.Apply(Beat(6)));
        Assert.Equal(KodoReduceResult.Applied, reducer.Apply(Change(7, "healthy", "failing")));

        Assert.Equal(7, reducer.Revision);
        Assert.Equal(KodoDisplayState.Failing, reducer.Display());
        Assert.Equal("failing", reducer.Status!.State);
        Assert.False(reducer.NeedsSnapshot);
    }

    [Fact]
    public void A_frozen_change_keeps_the_underlying_state()
    {
        var (reducer, _) = Make();
        reducer.Apply(Snapshot(1));

        reducer.Apply(Change(2, "healthy", "frozen", "failing"));

        Assert.Equal(KodoDisplayState.Frozen, reducer.Display());
        Assert.Equal("failing", reducer.Status!.UnderlyingState);
    }

    [Fact]
    public void A_skipped_revision_is_not_applied_and_asks_for_a_snapshot()
    {
        var (reducer, _) = Make();
        reducer.Apply(Snapshot(5));
        var requested = 0;
        reducer.SnapshotRequested += (_, _) => requested++;

        var result = reducer.Apply(Change(7, "healthy", "failing"));

        Assert.Equal(KodoReduceResult.GapDetected, result);
        Assert.Equal(5, reducer.Revision);
        Assert.Equal("healthy", reducer.Status!.State);
        Assert.True(reducer.NeedsSnapshot);
        Assert.Equal(1, requested);
        Assert.Equal(KodoDisplayState.Unobserved, reducer.Display());
    }

    [Fact]
    public void Deltas_after_a_gap_wait_for_the_snapshot_and_the_request_is_raised_once()
    {
        var (reducer, _) = Make();
        reducer.Apply(Snapshot(5));
        var requested = 0;
        reducer.SnapshotRequested += (_, _) => requested++;
        reducer.Apply(Beat(9));

        Assert.Equal(KodoReduceResult.AwaitingSnapshot, reducer.Apply(Beat(10)));
        Assert.Equal(KodoReduceResult.AwaitingSnapshot, reducer.Apply(Change(11, "healthy", "failing")));
        Assert.Equal(1, requested);

        Assert.Equal(KodoReduceResult.Applied, reducer.Apply(Snapshot(11, "failing")));
        Assert.Equal(KodoDisplayState.Failing, reducer.Display());
        Assert.Equal(KodoReduceResult.Applied, reducer.Apply(Beat(12)));
    }

    [Fact]
    public void A_delta_before_the_first_snapshot_is_not_applied()
    {
        var (reducer, _) = Make();

        Assert.Equal(KodoReduceResult.AwaitingSnapshot, reducer.Apply(Beat(1)));
        Assert.Null(reducer.Status);
        Assert.Equal(KodoDisplayState.Unobserved, reducer.Display());
    }

    [Fact]
    public void A_repeated_or_older_revision_is_a_duplicate()
    {
        var (reducer, _) = Make();
        reducer.Apply(Snapshot(5));
        reducer.Apply(Change(6, "healthy", "degraded"));

        Assert.Equal(KodoReduceResult.Duplicate, reducer.Apply(Change(6, "healthy", "degraded")));
        Assert.Equal(KodoReduceResult.Duplicate, reducer.Apply(Beat(3)));
        Assert.Equal(6, reducer.Revision);
        Assert.Equal(KodoDisplayState.Degraded, reducer.Display());
    }

    [Fact]
    public void A_snapshot_older_than_the_held_revision_is_not_applied()
    {
        var (reducer, _) = Make();
        reducer.Apply(Snapshot(8, "degraded"));

        Assert.Equal(KodoReduceResult.Duplicate, reducer.Apply(Snapshot(4, "healthy")));
        Assert.Equal(8, reducer.Revision);
        Assert.Equal(KodoDisplayState.Degraded, reducer.Display());
    }

    [Fact]
    public void A_state_change_that_does_not_follow_the_held_state_is_a_gap()
    {
        var (reducer, _) = Make();
        reducer.Apply(Snapshot(1, "healthy"));

        var result = reducer.Apply(Change(2, "degraded", "failing"));

        Assert.Equal(KodoReduceResult.GapDetected, result);
        Assert.Equal("healthy", reducer.Status!.State);
        Assert.True(reducer.NeedsSnapshot);
    }

    [Fact]
    public void Reconnecting_requires_a_new_snapshot_before_deltas()
    {
        var (reducer, _) = Make();
        reducer.Apply(Snapshot(1));
        var requested = 0;
        reducer.SnapshotRequested += (_, _) => requested++;

        reducer.Reconnected();

        Assert.True(reducer.NeedsSnapshot);
        Assert.Equal(1, requested);
        Assert.Equal(KodoDisplayState.Unobserved, reducer.Display());
        Assert.Equal(KodoReduceResult.AwaitingSnapshot, reducer.Apply(Beat(2)));
        Assert.Equal(KodoReduceResult.Applied, reducer.Apply(Snapshot(2)));
        Assert.Equal(KodoDisplayState.Healthy, reducer.Display());
    }

    [Theory]
    [InlineData("nonsense")]
    public void An_unknown_event_type_is_rejected(string type)
    {
        var (reducer, _) = Make();
        reducer.Apply(Snapshot(1));

        Assert.Equal(KodoReduceResult.Rejected, reducer.Apply(new KodoStreamEventDto(2, type, _start, null, null)));
        Assert.Equal(1, reducer.Revision);
    }

    [Fact]
    public void Unreadable_snapshots_and_changes_are_rejected_without_changing_state()
    {
        var (reducer, _) = Make();
        reducer.Apply(Snapshot(1));

        Assert.Equal(KodoReduceResult.Rejected, reducer.Apply(Snapshot(2, "melting")));
        Assert.Equal(KodoReduceResult.Rejected, reducer.Apply(new KodoStreamEventDto(2, KodoStreamEventDto.SnapshotType, _start, null, null)));
        Assert.Equal(KodoReduceResult.Rejected, reducer.Apply(Change(2, "healthy", "melting")));
        Assert.Equal(KodoReduceResult.Rejected, reducer.Apply(new KodoStreamEventDto(2, KodoStreamEventDto.StateChangedType, _start, null, null)));
        var future = new KodoStreamEventDto(2, KodoStreamEventDto.SnapshotType, _start, null,
            new KodoStatusSnapshotDto(99, 2, Status(), null, []));
        Assert.Equal(KodoReduceResult.Rejected, reducer.Apply(future));

        Assert.Equal(1, reducer.Revision);
        Assert.Equal(KodoDisplayState.Healthy, reducer.Display());
    }

    [Fact]
    public void Silence_longer_than_the_budget_turns_unobserved_and_exactly_the_budget_does_not()
    {
        var (reducer, time) = Make();
        reducer.Apply(Snapshot(1));

        time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(KodoDisplayState.Healthy, reducer.Display());

        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(KodoDisplayState.Unobserved, reducer.Display());
    }

    [Fact]
    public void A_heartbeat_keeps_the_display_observed()
    {
        var (reducer, time) = Make();
        reducer.Apply(Snapshot(1));

        time.Advance(TimeSpan.FromSeconds(25));
        reducer.Apply(Beat(2));
        time.Advance(TimeSpan.FromSeconds(25));

        Assert.Equal(KodoDisplayState.Healthy, reducer.Display());
        Assert.Equal(TimeSpan.FromSeconds(25), reducer.SilentFor());
    }

    [Fact]
    public void A_stale_green_never_survives_silence_after_recovery_of_the_stream()
    {
        var (reducer, time) = Make();
        reducer.Apply(Snapshot(1));
        time.Advance(TimeSpan.FromMinutes(10));

        Assert.Equal(KodoDisplayState.Unobserved, reducer.Display());
        reducer.Apply(Snapshot(1));
        Assert.Equal(KodoDisplayState.Healthy, reducer.Display());
    }

    [Fact]
    public void A_clock_step_back_counts_as_age_zero()
    {
        var time = new SettableTime(_start);
        var reducer = new KodoStreamReducer(time);
        reducer.Apply(Snapshot(1));

        time.Now = _start - TimeSpan.FromMinutes(5);

        Assert.Equal(KodoDisplayState.Healthy, reducer.Display());
        Assert.Equal(TimeSpan.Zero, reducer.SilentFor());
    }

    [Fact]
    public void Components_come_from_the_last_snapshot_and_changed_is_raised()
    {
        var (reducer, _) = Make();
        var changes = 0;
        reducer.Changed += (_, _) => changes++;

        reducer.Apply(Snapshot(1, components: [new KodoComponentDto("host.disk.free", "amber", "low", _start)]));
        reducer.Apply(Beat(2));

        Assert.Single(reducer.Components);
        Assert.Equal(2, changes);
    }
}
