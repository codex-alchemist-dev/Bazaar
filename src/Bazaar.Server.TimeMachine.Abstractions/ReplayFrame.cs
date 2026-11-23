namespace Bazaar.Server.TimeMachine;

/// <summary>One message as it stood at the cursor: its latest version at or below the effective seq.</summary>
/// <param name="MessageId">Stable message identity.</param>
/// <param name="AuthorId">Writer of the version shown.</param>
/// <param name="FirstSeq">Seq of the message's first event.</param>
/// <param name="VersionSeq">Seq of the event that created the version shown.</param>
/// <param name="Version">Version number shown.</param>
/// <param name="Edited">True when the version shown is not the first.</param>
/// <param name="Quarantined">True when a quarantine event at or below the effective seq targets the version shown.</param>
/// <param name="PayloadJson">Kind-specific payload of the version shown; the viewer's client decodes it.</param>
/// <param name="CreatedAt">When the version shown was written.</param>
public sealed record ReplayMessage(
    Guid MessageId,
    Guid AuthorId,
    long FirstSeq,
    long VersionSeq,
    int Version,
    bool Edited,
    bool Quarantined,
    string? PayloadJson,
    DateTimeOffset CreatedAt);

/// <summary>A task as the Kanban event log rebuilds it at a seq.</summary>
public sealed record ReplayTask(Guid TaskId, string Title, string Column);

/// <summary>The Vault state in effect: the nearest commit at or before the cursor (docs/design/18).</summary>
public sealed record ReplayVaultState(string Commit);

/// <summary>The Soul version in effect for one agent (docs/design/07).</summary>
public sealed record ReplaySoulVersion(Guid AgentId, int Version);

/// <summary>The reputation score in effect for one agent (docs/design/11).</summary>
public sealed record ReplayReputation(Guid AgentId, decimal Score);

/// <summary>A GC rendered at a point in its history. Read-only; nothing was re-executed.</summary>
/// <param name="Cursor">The cursor that was asked for.</param>
/// <param name="EffectiveSeq">The seq actually rendered: the cursor seq clamped to the viewer's grant and the GC's head.</param>
/// <param name="Clamped">True when <see cref="EffectiveSeq"/> is below the requested seq.</param>
/// <param name="Messages">Messages at their latest version at or below <see cref="EffectiveSeq"/>, in first-seq order.</param>
/// <param name="Kanban">Tasks then, or unavailable.</param>
/// <param name="Vault">The Vault commit then, or unavailable.</param>
/// <param name="Souls">Soul versions then, or unavailable.</param>
/// <param name="Reputation">Reputation then, or unavailable.</param>
public sealed record ReplayFrame(
    ReplayCursor Cursor,
    long EffectiveSeq,
    bool Clamped,
    IReadOnlyList<ReplayMessage> Messages,
    ReplayPart<IReadOnlyList<ReplayTask>> Kanban,
    ReplayPart<ReplayVaultState> Vault,
    ReplayPart<IReadOnlyList<ReplaySoulVersion>> Souls,
    ReplayPart<IReadOnlyList<ReplayReputation>> Reputation);
