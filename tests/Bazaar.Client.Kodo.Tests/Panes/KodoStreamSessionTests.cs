using Bazaar.Client.Kodo.Live;
using Microsoft.Extensions.Time.Testing;
using static Bazaar.Client.Kodo.Tests.Panes.KodoTestFrames;

namespace Bazaar.Client.Kodo.Tests.Panes;

public class KodoStreamSessionTests
{
    private sealed class SettableTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static (KodoStreamSession Session, FakeTimeProvider Time) Make()
    {
        var time = new FakeTimeProvider(Start);
        return (new KodoStreamSession(time), time);
    }

    [Fact]
    public void Before_any_snapshot_the_display_is_unobserved_and_a_snapshot_is_needed()
    {
        var (session, _) = Make();

        Assert.Equal("unobserved", session.Current().State);
        Assert.True(session.NeedsSnapshot);
        Assert.Equal(0, session.Revision);
        Assert.Null(session.LastEventAt);
        Assert.Null(session.SilentFor());
    }

    [Fact]
    public void A_snapshot_then_in_order_deltas_apply()
    {
        var (session, _) = Make();

        Assert.Equal(KodoApplyOutcome.Applied, session.Apply(Snapshot(5)).Outcome);
        Assert.Equal(KodoApplyOutcome.Applied, session.Apply(Beat(6)).Outcome);
        Assert.Equal(KodoApplyOutcome.Applied, session.Apply(Change(7, "healthy", "failing")).Outcome);

        Assert.Equal(7, session.Revision);
        Assert.Equal("failing", session.Current().State);
        Assert.False(session.NeedsSnapshot);
    }

    [Fact]
    public void A_skipped_revision_is_not_applied_asks_for_a_snapshot_and_shows_unobserved()
    {
        var (session, _) = Make();
        session.Apply(Snapshot(5));
        var requested = 0;
        session.SnapshotRequested += (_, _) => requested++;

        var result = session.Apply(Change(7, "healthy", "failing"));

        Assert.Equal(KodoApplyOutcome.NeedsSnapshot, result.Outcome);
        Assert.Equal(5, session.Revision);
        Assert.True(session.NeedsSnapshot);
        Assert.Equal(1, requested);
        Assert.Equal("unobserved", session.Current().State);
    }

    [Fact]
    public void Deltas_after_a_gap_wait_for_the_snapshot_and_the_request_is_raised_once()
    {
        var (session, _) = Make();
        session.Apply(Snapshot(5));
        var requested = 0;
        session.SnapshotRequested += (_, _) => requested++;
        session.Apply(Beat(9));

        // Revision 6 would follow the tracker's held revision 5, but a gap is open: it must not be applied.
        Assert.Equal(KodoApplyOutcome.NeedsSnapshot, session.Apply(Beat(6)).Outcome);
        Assert.Equal(KodoApplyOutcome.NeedsSnapshot, session.Apply(Change(11, "healthy", "failing")).Outcome);
        Assert.Equal(5, session.Revision);
        Assert.Equal(1, requested);

        Assert.Equal(KodoApplyOutcome.Applied, session.Apply(Snapshot(11, "failing")).Outcome);
        Assert.Equal("failing", session.Current().State);
        Assert.Equal(KodoApplyOutcome.Applied, session.Apply(Beat(12)).Outcome);
    }

    [Fact]
    public void A_delta_before_the_first_snapshot_is_not_applied()
    {
        var (session, _) = Make();

        Assert.Equal(KodoApplyOutcome.NeedsSnapshot, session.Apply(Beat(1)).Outcome);
        Assert.Equal("unobserved", session.Current().State);
        Assert.NotNull(session.LastEventAt);
    }

    [Fact]
    public void A_snapshot_older_than_the_held_revision_is_not_applied()
    {
        var (session, _) = Make();
        session.Apply(Snapshot(8, "degraded"));

        Assert.Equal(KodoApplyOutcome.Duplicate, session.Apply(Snapshot(4, "healthy")).Outcome);
        Assert.Equal(8, session.Revision);
        Assert.Equal("degraded", session.Current().State);
    }

    [Fact]
    public void A_snapshot_from_the_read_api_applies_and_clears_the_need()
    {
        var (session, _) = Make();

        Assert.Equal(KodoApplyOutcome.Applied, session.ApplySnapshot(SnapshotBody(3, "recovering", score: 61)).Outcome);

        Assert.False(session.NeedsSnapshot);
        Assert.Equal("recovering", session.Current().State);
        Assert.Equal(61, session.LastKnownScore);
        Assert.Equal(KodoApplyOutcome.Rejected, session.ApplySnapshot(null).Outcome);
    }

    [Fact]
    public void Reconnecting_requires_a_new_snapshot_before_deltas()
    {
        var (session, _) = Make();
        session.Apply(Snapshot(1));
        var requested = 0;
        session.SnapshotRequested += (_, _) => requested++;

        session.Reconnected();

        Assert.True(session.NeedsSnapshot);
        Assert.Equal(1, requested);
        Assert.Equal("unobserved", session.Current().State);
        Assert.Equal(KodoApplyOutcome.NeedsSnapshot, session.Apply(Beat(2)).Outcome);
        Assert.Equal(KodoApplyOutcome.Applied, session.Apply(Snapshot(2)).Outcome);
        Assert.Equal("healthy", session.Current().State);
    }

    [Fact]
    public void Unreadable_frames_are_rejected_without_changing_state_or_liveness()
    {
        var (session, time) = Make();
        session.Apply(Snapshot(1));
        time.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(KodoApplyOutcome.Rejected, session.Apply(new KodoFrameWire(2, "mystery", Start, null, null)).Outcome);
        Assert.Equal(KodoApplyOutcome.Rejected, session.Apply(Snapshot(2, "melting")).Outcome);
        Assert.Equal(KodoApplyOutcome.Rejected, session.Apply(Change(2, "healthy", "melting")).Outcome);

        Assert.Equal(1, session.Revision);
        Assert.Equal("healthy", session.Current().State);
        Assert.Equal(TimeSpan.FromSeconds(10), session.SilentFor());
    }

    [Fact]
    public void Silence_longer_than_the_budget_turns_unobserved_and_exactly_the_budget_does_not()
    {
        var (session, time) = Make();
        session.Apply(Snapshot(1));

        time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal("healthy", session.Current().State);

        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal("unobserved", session.Current().State);
    }

    [Fact]
    public void A_heartbeat_keeps_the_display_observed_and_resets_silence()
    {
        var (session, time) = Make();
        session.Apply(Snapshot(1));

        time.Advance(TimeSpan.FromSeconds(25));
        session.Apply(Beat(2));
        time.Advance(TimeSpan.FromSeconds(25));

        Assert.Equal("healthy", session.Current().State);
        Assert.Equal(TimeSpan.FromSeconds(25), session.SilentFor());
    }

    [Fact]
    public void A_stale_green_never_survives_silence_and_a_new_snapshot_restores_it()
    {
        var (session, time) = Make();
        session.Apply(Snapshot(1));
        time.Advance(TimeSpan.FromMinutes(10));

        Assert.Equal("unobserved", session.Current().State);
        session.Apply(Snapshot(1));
        Assert.Equal("healthy", session.Current().State);
    }

    [Fact]
    public void A_clock_step_back_counts_as_age_zero()
    {
        var time = new SettableTime(Start);
        var session = new KodoStreamSession(time);
        session.Apply(Snapshot(1));

        time.Now = Start - TimeSpan.FromMinutes(5);

        Assert.Equal("healthy", session.Current().State);
        Assert.Equal(TimeSpan.Zero, session.SilentFor());
    }

    [Fact]
    public void Components_and_score_come_from_the_last_snapshot_and_changed_is_raised_for_every_frame()
    {
        var (session, _) = Make();
        var changes = 0;
        session.Changed += (_, _) => changes++;

        session.Apply(Snapshot(1, components: [new KodoComponentWire("host.disk.free", "amber", "low", Start)]));
        session.Apply(Beat(2));
        session.Apply(Beat(3));

        Assert.Single(session.Current().Components);
        Assert.Equal(90, session.LastKnownScore);
        Assert.Equal(3, changes);
    }
}
