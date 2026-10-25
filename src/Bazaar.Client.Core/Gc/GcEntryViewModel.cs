using Bazaar.Contracts.Gc;

namespace Bazaar.Client.Core.Gc;

/// <summary>
/// Header fields shared by every timeline entry. Built by <see cref="GcEntryBuilder"/>.
/// </summary>
/// <param name="GcId">The group chat the entry belongs to.</param>
/// <param name="Seq">Sequence number of the entry's first event. The row key is (GcId, Seq).</param>
/// <param name="Kind">Event kind as the log stores it.</param>
/// <param name="AuthorId">Principal or system id that wrote the entry.</param>
/// <param name="AuthorKind">Who wrote the row.</param>
/// <param name="AuthorName">Display name from the principal list, or null when the principal is unknown.</param>
/// <param name="AuthorPrincipalKind">Principal kind from the principal list, or null when the principal is unknown.</param>
/// <param name="SourceTrust">Where the content came from.</param>
/// <param name="CreatedAt">When the server wrote the entry, in UTC.</param>
/// <param name="LocalTime">CreatedAt in the viewer's time zone.</param>
/// <param name="DateSeparator">The viewer-local day this entry opens, when a date separator goes above it; otherwise null.</param>
/// <param name="IsContinuation">True when the entry continues the previous entry's author group, so the head omits the header.</param>
public sealed record GcEntryHeader(
    Guid GcId,
    long Seq,
    string Kind,
    Guid AuthorId,
    AuthorKind AuthorKind,
    string? AuthorName,
    PrincipalKind? AuthorPrincipalKind,
    SourceTrust SourceTrust,
    DateTimeOffset CreatedAt,
    DateTimeOffset LocalTime,
    DateOnly? DateSeparator,
    bool IsContinuation);

/// <summary>
/// One row of a GC timeline as the client shows it. Immutable, built without I/O
/// (docs/design/27-client-architecture.md, GC message pipeline). Each kind has its own subtype.
/// </summary>
/// <param name="Header">Fields every entry shares.</param>
public abstract record GcEntryViewModel(GcEntryHeader Header);

/// <summary>
/// A message row. Edits are folded in: the row shows the current version and its "edited" marker.
/// </summary>
/// <param name="Header">Shared header; the kind is <see cref="Contracts.Gc.GcEventKinds.Message"/>.</param>
/// <param name="MessageId">Stable message identity across edits, or null when the event has none.</param>
/// <param name="Body">Text of the current version, or null when the message is not in the store.</param>
/// <param name="Version">Current version number, starting at 1.</param>
/// <param name="IsEdited">True when the message has more than one version.</param>
public sealed record GcMessageEntryViewModel(
    GcEntryHeader Header,
    Guid? MessageId,
    string? Body,
    int Version,
    bool IsEdited) : GcEntryViewModel(Header);

/// <summary>
/// A non-message event row: membership, archive and access notices, structured records, quarantine
/// notes, hand-overs and external mail metadata. The payload is passed through unparsed; each head
/// decides how to show it.
/// </summary>
/// <param name="Header">Shared header.</param>
/// <param name="PayloadJson">Kind-specific payload as JSON text, or null.</param>
/// <param name="TargetSeq">Earlier sequence number this event refers to, or null.</param>
/// <param name="ReplyToSeq">Sequence number this event replies to, or null.</param>
public sealed record GcEventEntryViewModel(
    GcEntryHeader Header,
    string? PayloadJson,
    long? TargetSeq,
    long? ReplyToSeq) : GcEntryViewModel(Header);

/// <summary>
/// A row whose kind this client does not know, such as a kind another doc registered later. The head
/// shows a placeholder card with the kind (docs/design/27-client-architecture.md, Behaviour).
/// </summary>
/// <param name="Header">Shared header; <see cref="GcEntryHeader.Kind"/> names the unknown kind.</param>
public sealed record GcUnknownEntryViewModel(GcEntryHeader Header) : GcEntryViewModel(Header);
