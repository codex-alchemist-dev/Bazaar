namespace Bazaar.Server.Agents.Data;

/// <summary>One row of <c>agent</c>.</summary>
public sealed record AgentRecord(Guid AgentId, string Name, Guid? SoulVersionId, AgentState State, DateTimeOffset CreatedAt);

/// <summary>
/// A new inbox item. A GC-backed item sets both <see cref="GcId"/> and <see cref="GcSeq"/>; any other item sets
/// <see cref="SourceRef"/>. <see cref="Priority"/> is computed by the caller from the kind (doc 06 priority order).
/// </summary>
public sealed record InboxEnqueue(
    Guid AgentId, string Kind, short Priority, Guid? GcId = null, long? GcSeq = null, string? SourceRef = null, DateTimeOffset? DueAt = null);

/// <summary>An inbox item returned by a claim.</summary>
public sealed record InboxRow(Guid InboxItemId, string Kind, short Priority, Guid? GcId, long? GcSeq, string? SourceRef);

/// <summary>The fields needed to start a turn. <see cref="ManifestJson"/> stores ids and hashes, never text (doc 06).</summary>
public sealed record TurnStart(
    Guid AgentId,
    Guid SessionId,
    TurnRole Role,
    string WakeReason,
    Guid? ParentTurnId = null,
    int Depth = 0,
    string ManifestJson = "{}");

/// <summary>One row of <c>turn</c>.</summary>
public sealed record TurnRecord(
    Guid TurnId,
    Guid AgentId,
    Guid SessionId,
    TurnRole Role,
    Guid? ParentTurnId,
    int Depth,
    string WakeReason,
    TurnStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string ContextManifestJson);

/// <summary>One row of <c>turn_step</c>.</summary>
public sealed record TurnStepRecord(
    Guid TurnId,
    int N,
    TurnStepKind Kind,
    TurnStepStatus Status,
    string RequestHash,
    string? ResponseJson,
    bool Idempotent,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt);

/// <summary>One row of <c>agent_lease</c>.</summary>
public sealed record LeaseRecord(Guid AgentId, string Holder, DateTimeOffset AcquiredAt, DateTimeOffset ExpiresAt);
