using Bazaar.Contracts.Gc;

namespace Bazaar.Server.Gc.Write;

/// <summary>
/// The sealed text of a message's first version. Encryption belongs to the personal-data service (doc 01 WP6), so the
/// writer stores the ciphertext it is given and never sees plaintext.
/// </summary>
/// <param name="BodyCiphertext">The encrypted body.</param>
/// <param name="KeyId">The key the body is sealed under.</param>
public sealed record MessageBody(ReadOnlyMemory<byte> BodyCiphertext, Guid KeyId);

/// <summary>
/// One event to append. Seq, event id and time are assigned by the writer; provenance is set by the caller from the
/// authenticated session, never from client input.
/// </summary>
/// <param name="GcId">The group chat.</param>
/// <param name="Kind">A registered event kind (<see cref="GcEventKinds"/>).</param>
/// <param name="AuthorKind">Who writes it.</param>
/// <param name="AuthorId">Principal or system id of the writer.</param>
/// <param name="SourceTrust">Where the content came from.</param>
/// <param name="ClientMsgId">Idempotency key, unique per author within the GC. A repeat returns the original event.</param>
public sealed record AppendRequest(
    Guid GcId,
    string Kind,
    AuthorKind AuthorKind,
    Guid AuthorId,
    SourceTrust SourceTrust,
    string ClientMsgId)
{
    /// <summary>Longest accepted <see cref="ClientMsgId"/>.</summary>
    public const int MaxClientMsgIdLength = 200;

    /// <summary>The writing session, when there is one.</summary>
    public Guid? SessionId { get; init; }

    /// <summary>Whether the writing session was tainted (doc 15).</summary>
    public bool TaintedAtWrite { get; init; }

    /// <summary>Taint lineage as a JSON array, or null.</summary>
    public string? TaintSourcesJson { get; init; }

    /// <summary>Earlier seq this event refers to.</summary>
    public long? TargetSeq { get; init; }

    /// <summary>Seq this event replies to.</summary>
    public long? ReplyToSeq { get; init; }

    /// <summary>Kind-specific payload as JSON text.</summary>
    public string? PayloadJson { get; init; }

    /// <summary>The message body; required for kind <c>message</c>, not allowed otherwise.</summary>
    public MessageBody? Message { get; init; }

    /// <summary>Principals mentioned. Already validated against membership by the caller (doc 01 WP8).</summary>
    public IReadOnlyList<Guid> Mentions { get; init; } = [];
}

/// <summary>The stored identity of an appended event.</summary>
/// <param name="GcId">The group chat.</param>
/// <param name="Seq">The event's gap-free seq.</param>
/// <param name="EventId">The event's id.</param>
/// <param name="Duplicate">True when the idempotency key had been used and this is the original event.</param>
/// <param name="CreatedAt">When the original event was written.</param>
public sealed record AppendResult(Guid GcId, long Seq, Guid EventId, bool Duplicate, DateTimeOffset CreatedAt);
