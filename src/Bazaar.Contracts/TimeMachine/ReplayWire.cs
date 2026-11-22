namespace Bazaar.Contracts.TimeMachine;

/// <summary>Request <c>replay.frame</c> (doc 02, replay and fork requests): render a GC as it stood at a seq. Read-only.</summary>
/// <param name="Gc">The group chat.</param>
/// <param name="Seq">Seq to render at. A value above the viewer's grant is clamped to it.</param>
/// <param name="Mode">Optional view mode; unset follows the install's <c>replay.default_view</c>.</param>
public sealed record ReplayFrameRequest(Guid Gc, long Seq, string? Mode = null);

/// <summary>Response to <c>replay.frame</c>.</summary>
/// <param name="Gc">The group chat.</param>
/// <param name="Seq">The seq actually rendered: the asked-for seq after the clamp to the viewer's grant.</param>
/// <param name="Head">The viewer's visible head, never the true head.</param>
/// <param name="Frame">The rendered frame.</param>
public sealed record ReplayFrameResult(Guid Gc, long Seq, long Head, ReplayFrameWire Frame);

/// <summary>A frame as it travels: messages plus the parts other modules own, each available or unavailable.</summary>
/// <param name="Messages">Messages at their latest version at or below the rendered seq, viewer-filtered, in first-seq order.</param>
/// <param name="Kanban">Tasks then.</param>
/// <param name="Vault">The Vault commit then.</param>
/// <param name="Souls">Soul versions then.</param>
/// <param name="Reputation">Reputation then.</param>
public sealed record ReplayFrameWire(
    IReadOnlyList<ReplayMessageWire> Messages,
    ReplayPartWire<IReadOnlyList<ReplayTaskWire>> Kanban,
    ReplayPartWire<ReplayVaultWire> Vault,
    ReplayPartWire<IReadOnlyList<ReplaySoulWire>> Souls,
    ReplayPartWire<IReadOnlyList<ReplayReputationWire>> Reputation);

/// <summary>One part of a frame: <c>available { value }</c> or <c>unavailable { reason }</c>.</summary>
/// <typeparam name="T">The part's value type.</typeparam>
/// <param name="Available">True when <paramref name="Value"/> holds the part.</param>
/// <param name="Value">The part, when available.</param>
/// <param name="Reason">Why the part is unavailable, when it is.</param>
public sealed record ReplayPartWire<T>(bool Available, T? Value, string? Reason);

/// <summary>One message as it stood at the rendered seq.</summary>
/// <param name="MessageId">Stable message id.</param>
/// <param name="AuthorId">Writer of the version shown.</param>
/// <param name="FirstSeq">Seq of the message's first event.</param>
/// <param name="VersionSeq">Seq of the event that made the version shown.</param>
/// <param name="Version">Version number shown.</param>
/// <param name="Edited">True when the version shown is not the first.</param>
/// <param name="Quarantined">True when the version shown was quarantined by then.</param>
/// <param name="PayloadJson">Kind-specific payload of the version shown; the client decodes it.</param>
/// <param name="CreatedAt">When the version shown was written.</param>
public sealed record ReplayMessageWire(
    Guid MessageId,
    Guid AuthorId,
    long FirstSeq,
    long VersionSeq,
    int Version,
    bool Edited,
    bool Quarantined,
    string? PayloadJson,
    DateTimeOffset CreatedAt);

/// <summary>A task as the Kanban log rebuilds it.</summary>
/// <param name="TaskId">Task id.</param>
/// <param name="Title">Title then.</param>
/// <param name="Column">Column then.</param>
public sealed record ReplayTaskWire(Guid TaskId, string Title, string Column);

/// <summary>The Vault commit in effect.</summary>
/// <param name="Commit">Commit id.</param>
public sealed record ReplayVaultWire(string Commit);

/// <summary>The Soul version in effect for one agent.</summary>
/// <param name="AgentId">Agent id.</param>
/// <param name="Version">Soul version.</param>
public sealed record ReplaySoulWire(Guid AgentId, int Version);

/// <summary>The reputation score in effect for one agent.</summary>
/// <param name="AgentId">Agent id.</param>
/// <param name="Score">Score then.</param>
public sealed record ReplayReputationWire(Guid AgentId, decimal Score);

/// <summary>Request <c>replay.fork</c>: a new project GC linked to the source. No messages are copied. Idempotent on (gc, effective seq, request id).</summary>
/// <param name="Gc">The source group chat.</param>
/// <param name="Seq">Seq to fork at; clamped like <see cref="ReplayFrameRequest.Seq"/>.</param>
/// <param name="RequestId">Caller-chosen idempotency key.</param>
public sealed record ReplayForkRequest(Guid Gc, long Seq, Guid RequestId);

/// <summary>Response to <c>replay.fork</c>.</summary>
/// <param name="NewGc">The new project GC.</param>
/// <param name="VaultCommit">The Vault commit the fork branches from, or null when the Vault is not installed.</param>
/// <param name="Created">False when a repeat returned the first result.</param>
public sealed record ReplayForkResult(Guid NewGc, string? VaultCommit, bool Created);

/// <summary>Request <c>replay.points</c>: the points in time a GC can be replayed at. Read-only.</summary>
/// <param name="Gc">The group chat.</param>
/// <param name="AfterSeq">Return points after this seq; 0 starts at the beginning.</param>
/// <param name="Limit">Most points to return; the server clamps it to at most 500.</param>
/// <param name="BucketSeconds">When set, thin points to the last event of each bucket of this many seconds; unset lists every event.</param>
public sealed record ReplayPointsRequest(Guid Gc, long AfterSeq = 0, int Limit = 100, int? BucketSeconds = null);

/// <summary>Response to <c>replay.points</c>.</summary>
/// <param name="Gc">The group chat.</param>
/// <param name="Points">Points in time order.</param>
/// <param name="Next">Pass as <c>afterSeq</c> for the next page, or null when complete.</param>
/// <param name="Head">The viewer's visible head, never the true head.</param>
public sealed record ReplayPointsResult(Guid Gc, IReadOnlyList<ReplayPointWire> Points, long? Next, long Head);

/// <summary>One point in time. Render a frame at <paramref name="Seq"/> with <c>replay.frame</c>.</summary>
/// <param name="Seq">Seq to render at.</param>
/// <param name="At">When the point happened.</param>
/// <param name="Kind">message, edit, quarantine, event, or audit (owner only).</param>
/// <param name="Label">A short label; never a payload.</param>
public sealed record ReplayPointWire(long Seq, DateTimeOffset At, string Kind, string Label);
