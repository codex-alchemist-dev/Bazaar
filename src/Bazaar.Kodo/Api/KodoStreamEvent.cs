namespace Bazaar.Kodo.Api;

/// <summary>
/// The displayed state moved (doc 46 section 15.2, topic <c>status</c>).
/// </summary>
/// <param name="Previous">The state before, as a wire string from <see cref="KodoWireNames.State"/>.</param>
/// <param name="State">The state now, as a wire string.</param>
/// <param name="Underlying">The underlying health state now, as a wire string. Differs from <paramref name="State"/> only while frozen.</param>
public sealed record KodoStateChange(string Previous, string State, string Underlying);

/// <summary>
/// One event on the <c>kodo</c> stream: a state change, a snapshot, or a heartbeat. The stream is state, not history (doc 02's
/// stream registry), so every event carries a <see cref="Revision"/>. The revision rises by exactly one per event on the
/// stream, so a client that sees a gap re-fetches the snapshot (doc 46 section 15.2).
/// </summary>
/// <param name="Revision">The stream revision. The first event is 1, and each later event is one more.</param>
/// <param name="Type">One of <see cref="StateChangedType"/>, <see cref="SnapshotType"/> or <see cref="HeartbeatType"/>.</param>
/// <param name="At">When the event was made, in UTC.</param>
/// <param name="StateChange">The state change, present for <see cref="StateChangedType"/> only, otherwise null.</param>
/// <param name="Snapshot">The snapshot, present for <see cref="SnapshotType"/> only, otherwise null.</param>
public sealed record KodoStreamEvent(
    long Revision,
    string Type,
    DateTimeOffset At,
    KodoStateChange? StateChange,
    KodoStatusSnapshot? Snapshot)
{
    /// <summary>A change of the displayed state.</summary>
    public const string StateChangedType = "state_changed";

    /// <summary>A full status snapshot, sent on subscribe and on any doubt about the revision.</summary>
    public const string SnapshotType = "snapshot";

    /// <summary>A liveness frame. Clients treat 30 seconds without any event as <c>unobserved</c> (doc 46 section 8.3).</summary>
    public const string HeartbeatType = "heartbeat";
}
