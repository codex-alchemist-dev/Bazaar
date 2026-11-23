namespace Bazaar.Contracts.Wire;

/// <summary>
/// One agent's presence row as clients see it (docs/design/23, <c>agent_activity</c>). <paramref name="Revision"/> is the per-agent,
/// monotonic value the client orders deltas by.
/// </summary>
/// <param name="AgentId">The agent.</param>
/// <param name="Revision">Per-agent monotonic revision.</param>
/// <param name="Availability">Availability state name from doc 09, such as <c>OnShift</c>.</param>
/// <param name="Overlays">Overlay names shown beside the label, such as <c>Throttled</c>; empty when none.</param>
/// <param name="Presence">The presence label, one of the fixed set (DECISIONS-LOG C43), or <c>unknown</c>.</param>
/// <param name="ActivityKind">The enumerated activity kind: <c>Idle</c>, <c>ModelCall</c>, <c>Tool</c>, <c>Waiting</c> or <c>Drafting</c>.</param>
/// <param name="GcId">The GC of the current turn, when there is one.</param>
/// <param name="TurnId">The current turn, when there is one.</param>
/// <param name="Label">A short model-written label of at most 80 characters, or null when the operator allows kinds only.</param>
/// <param name="Since">When the row last changed its label.</param>
/// <param name="UpdatedAt">When the row was last written.</param>
public sealed record WirePresenceRow(
    Guid AgentId,
    long Revision,
    string Availability,
    IReadOnlyList<string> Overlays,
    string Presence,
    string ActivityKind,
    Guid? GcId,
    Guid? TurnId,
    string? Label,
    DateTimeOffset Since,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Event <c>presence.update</c> (stream <c>presence</c>) with kind <see cref="PresenceKinds.Agent"/>: the new state of one agent's row.
/// A client applies it only when <c>Revision</c> is greater than the revision it holds for the agent (docs/design/23).
/// </summary>
/// <param name="Kind"><see cref="PresenceKinds.Agent"/> for this payload. Receivers ignore kinds they do not know.</param>
/// <param name="AgentId">The agent; the same as <c>Row.AgentId</c>, so a receiver can order before reading the row.</param>
/// <param name="Revision">The row's revision; the same as <c>Row.Revision</c>.</param>
/// <param name="Row">The row after the change.</param>
[WirePayload(WireTypes.PresenceUpdate)]
public sealed record PresenceUpdate(string Kind, Guid AgentId, long Revision, WirePresenceRow Row);

/// <summary>Request <c>presence.snapshot</c>: every agent's current row. Carries nothing.</summary>
[WirePayload(WireTypes.PresenceSnapshot)]
public sealed record PresenceSnapshotRequest();

/// <summary>Response to <c>presence.snapshot</c>. Each row carries its revision, so the client resumes deltas from it.</summary>
/// <param name="Rows">One row per agent.</param>
[WirePayload]
public sealed record PresenceSnapshot(IReadOnlyList<WirePresenceRow> Rows);
