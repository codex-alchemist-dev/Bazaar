using System.Text;
using Bazaar.Client.Kodo.Live;

namespace Bazaar.Client.Kodo.Tests.Live;

public class KodoStreamTrackerTests
{
    private static readonly DateTimeOffset _t0 = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    private static KodoSnapshotWire Snapshot(long revision, string state = "healthy", string underlying = "healthy", int schema = 1) =>
        new(schema, revision, _t0, state, underlying, 92, null, [new KodoComponentWire("host.disk.free", "green", null, _t0)]);

    private static KodoFrameWire SnapshotFrame(long revision, string state = "healthy") =>
        new(revision, "snapshot", _t0, null, Snapshot(revision, state));

    private static KodoFrameWire Heartbeat(long revision) => new(revision, "heartbeat", _t0, null, null);

    private static KodoFrameWire Change(long revision, string state, string underlying) =>
        new(revision, "state_changed", _t0, new KodoStateChangeWire("healthy", state, underlying), null);

    private static KodoStreamTracker Tracked(long revision = 5)
    {
        var tracker = new KodoStreamTracker();
        Assert.Equal(KodoApplyOutcome.Applied, tracker.Apply(SnapshotFrame(revision), _t0).Outcome);
        return tracker;
    }

    [Fact]
    public void Nothing_received_is_unobserved() =>
        Assert.Equal("unobserved", new KodoStreamTracker().Current(_t0).State);

    [Fact]
    public void A_snapshot_shows_its_state_score_and_components()
    {
        KodoMonitorState state = Tracked().Current(_t0.AddSeconds(1));

        Assert.Equal("healthy", state.State);
        Assert.Equal(92, state.Score);
        Assert.Equal("host.disk.free", Assert.Single(state.Components).Id);
    }

    [Fact]
    public void The_next_revision_applies_and_a_state_change_moves_the_state()
    {
        KodoStreamTracker tracker = Tracked();

        Assert.Equal(KodoApplyOutcome.Applied, tracker.Apply(Change(6, "frozen", "failing"), _t0.AddSeconds(2)).Outcome);

        KodoMonitorState state = tracker.Current(_t0.AddSeconds(3));
        Assert.Equal("frozen", state.State);
        Assert.Equal("failing", state.Underlying);
        Assert.Equal(6, tracker.Revision);
    }

    [Fact]
    public void A_skipped_revision_asks_for_a_new_snapshot_and_changes_nothing()
    {
        KodoStreamTracker tracker = Tracked();

        KodoApplyResult result = tracker.Apply(Change(7, "failing", "failing"), _t0.AddSeconds(2));

        Assert.Equal(KodoApplyOutcome.NeedsSnapshot, result.Outcome);
        Assert.Equal("healthy", tracker.Current(_t0.AddSeconds(3)).State);
        Assert.Equal(5, tracker.Revision);
    }

    [Fact]
    public void A_delta_before_any_snapshot_asks_for_one() =>
        Assert.Equal(KodoApplyOutcome.NeedsSnapshot, new KodoStreamTracker().Apply(Heartbeat(1), _t0).Outcome);

    [Fact]
    public void A_repeated_revision_is_a_duplicate_that_still_counts_as_liveness()
    {
        KodoStreamTracker tracker = Tracked();

        Assert.Equal(KodoApplyOutcome.Duplicate, tracker.Apply(Heartbeat(5), _t0.AddSeconds(25)).Outcome);

        // 40 s after the snapshot but only 15 s after the last frame: still observed.
        Assert.Equal("healthy", tracker.Current(_t0.AddSeconds(40)).State);
    }

    [Theory]
    [InlineData(30, "healthy")]
    [InlineData(31, "unobserved")]
    public void Silence_past_the_heartbeat_budget_is_unobserved_and_exactly_the_budget_is_not(int seconds, string expected) =>
        Assert.Equal(expected, Tracked().Current(_t0.AddSeconds(seconds)).State);

    [Fact]
    public void An_old_snapshot_is_unobserved_even_while_heartbeats_arrive()
    {
        KodoStreamTracker tracker = Tracked();
        for (int i = 1; i <= 7; i++)
        {
            Assert.Equal(KodoApplyOutcome.Applied, tracker.Apply(Heartbeat(5 + i), _t0.AddSeconds(10 * i)).Outcome);
        }

        Assert.Equal("healthy", tracker.Current(_t0.AddSeconds(60)).State);
        Assert.Equal("unobserved", tracker.Current(_t0.AddSeconds(61)).State);
    }

    [Fact]
    public void A_clock_that_steps_back_counts_as_age_zero() =>
        Assert.Equal("healthy", Tracked().Current(_t0.AddHours(-1)).State);

    [Fact]
    public void Unobserved_hides_the_last_score_and_components() =>
        Assert.Empty(Tracked().Current(_t0.AddMinutes(5)).Components);

    [Theory]
    [InlineData("exploding")]
    [InlineData("unobserved")]
    public void An_unknown_or_client_only_state_is_rejected(string state)
    {
        KodoStreamTracker tracker = Tracked();

        KodoApplyResult result = tracker.Apply(Change(6, state, "healthy"), _t0.AddSeconds(1));

        Assert.Equal(KodoApplyOutcome.Rejected, result.Outcome);
        Assert.Equal("healthy", tracker.Current(_t0.AddSeconds(2)).State);
    }

    [Fact]
    public void An_unsupported_schema_is_rejected() =>
        Assert.Equal(KodoApplyOutcome.Rejected, new KodoStreamTracker().Apply(new KodoFrameWire(1, "snapshot", _t0, null, Snapshot(1, schema: 2)), _t0).Outcome);

    [Fact]
    public void An_unknown_frame_type_is_rejected() =>
        Assert.Equal(KodoApplyOutcome.Rejected, Tracked().Apply(new KodoFrameWire(6, "mystery", _t0, null, null), _t0).Outcome);

    [Fact]
    public void Frames_decode_from_the_servers_json()
    {
        const string json = """
            {"revision":7,"type":"snapshot","at":"2026-11-18T12:00:00+00:00","stateChange":null,"snapshot":
            {"schemaVersion":1,"revision":7,"status":{"state":"degraded","underlyingState":null,"score":71,"asOf":"2026-11-18T12:00:00+00:00","controls":[]},"journalHead":null,
            "components":[{"id":"host.disk.free","state":"amber","cause":"disk_low","since":"2026-11-18T11:00:00+00:00"}]}}
            """;

        Assert.True(KodoStreamCodec.TryReadFrame(Encoding.UTF8.GetBytes(json), out KodoFrameWire? frame, out _));
        var tracker = new KodoStreamTracker();
        Assert.Equal(KodoApplyOutcome.Applied, tracker.Apply(frame!, _t0).Outcome);
        KodoMonitorState state = tracker.Current(_t0);
        Assert.Equal("degraded", state.State);
        Assert.Equal("disk_low", Assert.Single(state.Components).Cause);
    }

    [Fact]
    public void Malformed_json_is_refused_with_a_reason()
    {
        Assert.False(KodoStreamCodec.TryReadFrame("{not json"u8, out KodoFrameWire? frame, out string? error));
        Assert.Null(frame);
        Assert.NotNull(error);
    }
}
