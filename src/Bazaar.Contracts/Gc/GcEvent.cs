namespace Bazaar.Contracts.Gc;

/// <summary>
/// One row of a group chat's append-only log. <see cref="Seq"/> is gap-free per GC. Every field
/// except the payload is assigned by the server.
/// </summary>
/// <param name="GcId">The group chat.</param>
/// <param name="Seq">Gap-free sequence number within the GC, starting at 1.</param>
/// <param name="EventId">Globally unique event id.</param>
/// <param name="Kind">Registry-backed kind; see <see cref="GcEventKinds"/>.</param>
/// <param name="AuthorKind">Who wrote the row.</param>
/// <param name="AuthorId">Principal or system id of the writer.</param>
/// <param name="SourceTrust">Where the content came from.</param>
/// <param name="TaintedAtWrite">Whether the writing session was tainted when it wrote this row.</param>
/// <param name="TaintSourcesJson">Taint lineage as a JSON array of source ids, or null when untainted.</param>
/// <param name="MessageId">Message this event belongs to, for message kinds.</param>
/// <param name="Version">Message version this event created, for message kinds.</param>
/// <param name="TargetSeq">Earlier sequence number this event refers to, such as a quarantine.</param>
/// <param name="ReplyToSeq">Sequence number this event replies to.</param>
/// <param name="PayloadJson">Kind-specific payload as JSON text, or null.</param>
/// <param name="CreatedAt">When the server wrote the row.</param>
public sealed record GcEvent(
    Guid GcId,
    long Seq,
    Guid EventId,
    string Kind,
    AuthorKind AuthorKind,
    Guid AuthorId,
    SourceTrust SourceTrust,
    bool TaintedAtWrite,
    string? TaintSourcesJson,
    Guid? MessageId,
    int? Version,
    long? TargetSeq,
    long? ReplyToSeq,
    string? PayloadJson,
    DateTimeOffset CreatedAt);
