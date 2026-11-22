using Bazaar.Contracts.Gc;

namespace Bazaar.Server.Gc.Model;

/// <summary>Role a member holds in a GC. The owner reads every GC implicitly and has no row.</summary>
public enum GcMemberRole
{
    /// <summary>Full member: reads and posts.</summary>
    Member,

    /// <summary>Observer: reads only.</summary>
    Observer,
}

/// <summary>The GC header fields the pure policy code needs (docs/design/01-gc-data-model.md).</summary>
/// <param name="GcId">The group chat.</param>
/// <param name="ParentGcId">Parent GC, or null for a top-level GC.</param>
/// <param name="TypeKey">Registry key such as <c>general</c>, <c>dm</c> or <c>thinking</c>.</param>
/// <param name="ArchivedAt">Archive time, or null when the GC is live.</param>
public sealed record GcRecord(Guid GcId, Guid? ParentGcId, string TypeKey, DateTimeOffset? ArchivedAt);

/// <summary>A membership row. Rows are never updated: leaving sets <see cref="LeftSeq"/> on a new state.</summary>
/// <param name="GcId">The group chat.</param>
/// <param name="PrincipalId">Member principal.</param>
/// <param name="Kind">Human, agent or owner.</param>
/// <param name="Role">Member or observer.</param>
/// <param name="JoinedSeq">Sequence number at which the principal joined.</param>
/// <param name="LeftSeq">Sequence number at which the principal left, or null while active.</param>
public sealed record GcMemberRecord(
    Guid GcId,
    Guid PrincipalId,
    PrincipalKind Kind,
    GcMemberRole Role,
    long JoinedSeq,
    long? LeftSeq);

/// <summary>Who is asking to read or write. Built from an authenticated session, never from client input.</summary>
/// <param name="PrincipalId">The caller.</param>
/// <param name="Kind">Owner, human or agent.</param>
public sealed record GcViewer(Guid PrincipalId, PrincipalKind Kind);

/// <summary>
/// A row a pure planner wants appended. The writer (Gc/Write) assigns seq, event id and server
/// fields, then stores it. Planners never touch the database.
/// </summary>
/// <param name="GcId">Target GC.</param>
/// <param name="Kind">Registry kind from <see cref="GcEventKinds"/>.</param>
/// <param name="AuthorKind">Who wrote the row.</param>
/// <param name="AuthorId">Writer principal or system id.</param>
/// <param name="SourceTrust">Where the content came from.</param>
/// <param name="MessageId">Message this row belongs to, for message kinds.</param>
/// <param name="Version">Message version this row creates, for message kinds.</param>
/// <param name="TargetSeq">Earlier seq this row refers to, such as a quarantine.</param>
/// <param name="PayloadJson">Kind-specific JSON payload, or null.</param>
public sealed record GcEventDraft(
    Guid GcId,
    string Kind,
    AuthorKind AuthorKind,
    Guid AuthorId,
    SourceTrust SourceTrust,
    Guid? MessageId = null,
    int? Version = null,
    long? TargetSeq = null,
    string? PayloadJson = null);

/// <summary>A stored message version as the history reader sees it.</summary>
/// <param name="GcId">The group chat.</param>
/// <param name="MessageId">Stable message identity.</param>
/// <param name="Version">Version number, starting at 1.</param>
/// <param name="Body">Full text of this version.</param>
/// <param name="EditorId">Principal who wrote this version.</param>
/// <param name="Seq">Sequence number of the event that created this version.</param>
/// <param name="CreatedAt">When this version was written.</param>
public sealed record MessageVersionRecord(
    Guid GcId,
    Guid MessageId,
    int Version,
    string Body,
    Guid EditorId,
    long Seq,
    DateTimeOffset CreatedAt);
