namespace Bazaar.Contracts.Kodo;

/// <summary>
/// The displayed state moved (doc 46 section 15.2, topic <c>status</c>).
/// </summary>
/// <param name="Previous">The state before, in the wire spelling of section 8.1.</param>
/// <param name="State">The state now, in the wire spelling.</param>
/// <param name="UnderlyingState">The underlying health state, set only when <paramref name="State"/> is <c>frozen</c>, as in <see cref="KodoStatusDto"/>.</param>
public sealed record KodoStateChangeDto(string Previous, string State, string? UnderlyingState);

/// <summary>
/// One event on the <c>kodo</c> stream: a state change, a snapshot, or a heartbeat. The stream is state, not history (doc 02's
/// stream registry), so every event carries a <see cref="Revision"/>. Only deltas (state changes and heartbeats) raise the
/// revision, by exactly one each, so a client that sees a gap re-fetches the snapshot (doc 46 section 15.2). A snapshot carries
/// the current revision and does not raise it.
/// </summary>
/// <param name="Revision">The stream revision. For a snapshot, the last delta's revision; otherwise one more than the one before.</param>
/// <param name="Type">One of <see cref="StateChangedType"/>, <see cref="SnapshotType"/> or <see cref="HeartbeatType"/>.</param>
/// <param name="At">When the event was made, in UTC.</param>
/// <param name="StateChange">The state change, present for <see cref="StateChangedType"/> only, otherwise null.</param>
/// <param name="Snapshot">The snapshot, present for <see cref="SnapshotType"/> only, otherwise null.</param>
public sealed record KodoStreamEventDto(
    long Revision,
    string Type,
    DateTimeOffset At,
    KodoStateChangeDto? StateChange,
    KodoStatusSnapshotDto? Snapshot)
{
    /// <summary>A change of the displayed state.</summary>
    public const string StateChangedType = "state_changed";

    /// <summary>A full status snapshot, sent on subscribe and on any doubt about the revision.</summary>
    public const string SnapshotType = "snapshot";

    /// <summary>A liveness frame. Clients treat 30 seconds without any event as <c>unobserved</c> (doc 46 section 8.3).</summary>
    public const string HeartbeatType = "heartbeat";
}
