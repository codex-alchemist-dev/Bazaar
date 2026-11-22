using Bazaar.Client.Kodo.Live;

namespace Bazaar.Client.Kodo.Tests.Panes;

/// <summary>Builds wire frames for the pane tests.</summary>
internal static class KodoTestFrames
{
    internal static readonly DateTimeOffset Start = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    internal static KodoSnapshotWire SnapshotBody(
        long revision,
        string state = "healthy",
        string? underlying = null,
        int score = 90,
        IReadOnlyList<KodoComponentWire>? components = null) =>
        new(1, revision, Start, state, underlying ?? state, score, null, components ?? []);

    internal static KodoFrameWire Snapshot(
        long revision,
        string state = "healthy",
        string? underlying = null,
        int score = 90,
        IReadOnlyList<KodoComponentWire>? components = null) =>
        new(revision, "snapshot", Start, null, SnapshotBody(revision, state, underlying, score, components));

    internal static KodoFrameWire Change(long revision, string previous, string state, string? underlying = null) =>
        new(revision, "state_changed", Start, new KodoStateChangeWire(previous, state, underlying ?? state), null);

    internal static KodoFrameWire Beat(long revision) => new(revision, "heartbeat", Start, null, null);

    internal static KodoControlRow Control(KodoControl control, bool enabled = true, string? reason = null) =>
        new(control, control switch
        {
            KodoControl.Restart => "Restart",
            KodoControl.Isolate => "Isolate",
            KodoControl.RollBack => "Roll back",
            _ => "Exit incident mode",
        }, enabled, reason);
}
