using Bazaar.Contracts.Wire;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Client.Presence.Tests;

/// <summary>The client reorder rule of docs/design/23, "Sequence numbers on deltas".</summary>
public class PresenceReceiverTests
{
    private static readonly Guid _ada = Guid.Parse("00000000-0000-7000-8000-00000000000a");
    private static readonly Guid _bob = Guid.Parse("00000000-0000-7000-8000-00000000000b");
    private static readonly DateTimeOffset _t = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);

    internal static WirePresenceRow Row(Guid agent, long revision, string presence = "working") =>
        new(agent, revision, "OnShift", [], presence, "Idle", null, null, null, _t, _t);

    internal static PresenceUpdate Delta(Guid agent, long revision, string presence = "working") =>
        new(PresenceKinds.Agent, agent, revision, Row(agent, revision, presence));

    private sealed class Rig : IDisposable
    {
        public Rig(int windowMs = 500)
        {
            Receiver = new PresenceReceiver(Fetch, new PresenceReceiverOptions { ReorderWindowMs = windowMs }, Time);
            Receiver.Changed += id => Changed.Add(id);
        }

        public FakeTimeProvider Time { get; } = new(_t);

        public PresenceReceiver Receiver { get; }

        public List<Guid> Changed { get; } = [];

        public PresenceSnapshot Snapshot { get; set; } = new([]);

        public bool FailSnapshot { get; set; }

        private Task<PresenceSnapshot> Fetch(CancellationToken ct) =>
            FailSnapshot ? Task.FromException<PresenceSnapshot>(new IOException("offline")) : Task.FromResult(Snapshot);

        public void Dispose() => Receiver.Dispose();
    }

    [Fact]
    public void In_order_deltas_apply_and_raise_changed_per_agent()
    {
        using var rig = new Rig();

        rig.Receiver.Apply(Delta(_ada, 1, "sleeping"));
        rig.Receiver.Apply(Delta(_ada, 2, "thinking"));
        rig.Receiver.Apply(Delta(_bob, 1));

        Assert.Equal("thinking", rig.Receiver.TryGetRow(_ada)!.Presence);
        Assert.Equal(2, rig.Receiver.TryGetRow(_ada)!.Revision);
        Assert.Equal([_ada, _ada, _bob], rig.Changed);
        Assert.Equal(2, rig.Receiver.All.Count);
    }

    [Fact]
    public void Stale_and_duplicate_deltas_are_dropped_without_a_change()
    {
        using var rig = new Rig();
        rig.Receiver.Apply(Delta(_ada, 4, "thinking"));
        rig.Changed.Clear();

        rig.Receiver.Apply(Delta(_ada, 4, "other"));
        rig.Receiver.Apply(Delta(_ada, 2, "sleeping"));

        Assert.Equal("thinking", rig.Receiver.TryGetRow(_ada)!.Presence);
        Assert.Empty(rig.Changed);
        Assert.Equal(0, rig.Receiver.SnapshotRequests);
    }

    [Fact]
    public void A_delta_for_an_agent_not_held_yet_applies_at_any_revision()
    {
        using var rig = new Rig();

        rig.Receiver.Apply(Delta(_ada, 17, "thinking"));

        Assert.Equal(17, rig.Receiver.TryGetRow(_ada)!.Revision);
    }

    [Fact]
    public void Unknown_kinds_are_ignored()
    {
        using var rig = new Rig();

        rig.Receiver.Apply(new PresenceUpdate("typing", _ada, 1, Row(_ada, 1)));

        Assert.Null(rig.Receiver.TryGetRow(_ada));
    }

    [Fact]
    public async Task A_missing_delta_arriving_inside_the_window_fills_the_gap_without_a_snapshot()
    {
        using var rig = new Rig();
        rig.Receiver.Apply(Delta(_ada, 1, "sleeping"));

        rig.Receiver.Apply(Delta(_ada, 3, "reviewing"));
        Assert.Equal(1, rig.Receiver.TryGetRow(_ada)!.Revision);

        rig.Time.Advance(TimeSpan.FromMilliseconds(300));
        rig.Receiver.Apply(Delta(_ada, 2, "thinking"));

        Assert.Equal(3, rig.Receiver.TryGetRow(_ada)!.Revision);
        Assert.Equal("reviewing", rig.Receiver.TryGetRow(_ada)!.Presence);

        rig.Time.Advance(TimeSpan.FromSeconds(5));
        await rig.Receiver.WhenIdleAsync();
        Assert.Equal(0, rig.Receiver.SnapshotRequests);
    }

    [Fact]
    public void Deltas_that_arrive_in_reverse_order_apply_in_revision_order()
    {
        using var rig = new Rig();
        rig.Receiver.Apply(Delta(_ada, 1));

        rig.Receiver.Apply(Delta(_ada, 4, "d"));
        rig.Receiver.Apply(Delta(_ada, 3, "c"));
        Assert.Equal(1, rig.Receiver.TryGetRow(_ada)!.Revision);
        rig.Receiver.Apply(Delta(_ada, 2, "b"));

        Assert.Equal(4, rig.Receiver.TryGetRow(_ada)!.Revision);
        Assert.Equal("d", rig.Receiver.TryGetRow(_ada)!.Presence);
    }

    [Fact]
    public async Task A_gap_that_outlives_the_window_fetches_the_snapshot_and_applies_it()
    {
        using var rig = new Rig();
        rig.Snapshot = new PresenceSnapshot([Row(_ada, 5, "researching"), Row(_bob, 2, "sleeping")]);
        rig.Receiver.Apply(Delta(_ada, 1));
        rig.Receiver.Apply(Delta(_ada, 4, "thinking"));

        rig.Time.Advance(TimeSpan.FromMilliseconds(499));
        await rig.Receiver.WhenIdleAsync();
        Assert.Equal(0, rig.Receiver.SnapshotRequests);

        rig.Time.Advance(TimeSpan.FromMilliseconds(1));
        await rig.Receiver.WhenIdleAsync();

        Assert.Equal(1, rig.Receiver.SnapshotRequests);
        Assert.Equal("researching", rig.Receiver.TryGetRow(_ada)!.Presence);
        Assert.Equal(5, rig.Receiver.TryGetRow(_ada)!.Revision);
        Assert.Equal("sleeping", rig.Receiver.TryGetRow(_bob)!.Presence);
    }

    [Fact]
    public async Task A_zero_window_fetches_the_snapshot_at_once()
    {
        using var rig = new Rig(windowMs: 0);
        rig.Snapshot = new PresenceSnapshot([Row(_ada, 9, "thinking")]);
        rig.Receiver.Apply(Delta(_ada, 1));

        rig.Receiver.Apply(Delta(_ada, 5));
        await rig.Receiver.WhenIdleAsync();

        Assert.Equal(9, rig.Receiver.TryGetRow(_ada)!.Revision);
        Assert.Equal(1, rig.Receiver.SnapshotRequests);
    }

    [Fact]
    public void A_snapshot_replaces_rows_even_at_a_lower_revision_after_a_server_restart()
    {
        using var rig = new Rig();
        rig.Receiver.Apply(Delta(_ada, 50, "thinking"));

        rig.Receiver.ApplySnapshot(new PresenceSnapshot([Row(_ada, 2, "sleeping")]));
        rig.Receiver.Apply(Delta(_ada, 3, "working"));

        Assert.Equal("working", rig.Receiver.TryGetRow(_ada)!.Presence);
        Assert.Equal(3, rig.Receiver.TryGetRow(_ada)!.Revision);
    }

    [Fact]
    public void A_snapshot_removes_rows_it_does_not_hold_and_raises_changed_only_for_differences()
    {
        using var rig = new Rig();
        rig.Receiver.Apply(Delta(_ada, 1, "same"));
        rig.Receiver.Apply(Delta(_bob, 1));
        rig.Changed.Clear();

        rig.Receiver.ApplySnapshot(new PresenceSnapshot([Row(_ada, 1, "same")]));

        Assert.Null(rig.Receiver.TryGetRow(_bob));
        Assert.Equal([_bob], rig.Changed);
    }

    [Fact]
    public void Held_deltas_that_continue_a_snapshot_are_kept()
    {
        using var rig = new Rig();
        rig.Receiver.Apply(Delta(_ada, 1));
        rig.Receiver.Apply(Delta(_ada, 6, "newer"));

        rig.Receiver.ApplySnapshot(new PresenceSnapshot([Row(_ada, 5, "snap")]));

        Assert.Equal(6, rig.Receiver.TryGetRow(_ada)!.Revision);
        Assert.Equal("newer", rig.Receiver.TryGetRow(_ada)!.Presence);
    }

    [Fact]
    public async Task A_failed_snapshot_leaves_the_held_rows_alone()
    {
        using var rig = new Rig();
        rig.FailSnapshot = true;
        rig.Receiver.Apply(Delta(_ada, 1, "sleeping"));
        rig.Receiver.Apply(Delta(_ada, 4));

        rig.Time.Advance(TimeSpan.FromSeconds(1));
        await rig.Receiver.WhenIdleAsync();

        Assert.Equal("sleeping", rig.Receiver.TryGetRow(_ada)!.Presence);
        Assert.Equal(1, rig.Receiver.SnapshotRequests);
    }

    [Fact]
    public void A_window_outside_zero_to_5000_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PresenceReceiver(_ => Task.FromResult(new PresenceSnapshot([])), new PresenceReceiverOptions { ReorderWindowMs = 5001 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PresenceReceiver(_ => Task.FromResult(new PresenceSnapshot([])), new PresenceReceiverOptions { ReorderWindowMs = -1 }));
    }
}
