using Bazaar.Contracts.Gc;
using Bazaar.Souls.Abstractions;

namespace Bazaar.Server.Gc.Write;

/// <summary>
/// The sealed text of a message's first version. Encryption belongs to the personal-data service (doc 01 WP6), so the
/// writer stores the ciphertext it is given and never sees plaintext.
/// </summary>
/// <param name="BodyCiphertext">The encrypted body.</param>
/// <param name="KeyId">The key the body is sealed under.</param>
/// <param name="Nonce">The cipher nonce, when the cipher uses one.</param>
public sealed record MessageBody(ReadOnlyMemory<byte> BodyCiphertext, Guid KeyId, ReadOnlyMemory<byte> Nonce = default);

/// <summary>
/// The sealed form of an event's payload: ciphertext plus the id of the key it is sealed under. The log stores the bytes
/// it is given and never holds a key (doc 01, doc 61). Both parts are always present together.
/// </summary>
/// <param name="Ciphertext">The sealed bytes. Not empty.</param>
/// <param name="KeyId">The key the bytes are sealed under.</param>
public sealed record SealedPayload(ReadOnlyMemory<byte> Ciphertext, Guid KeyId);

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

    /// <summary>
    /// Whether the event was tainted when written (doc 15). Null lets the trust hook derive it from the author kind and
    /// source trust; an explicit value is kept as given.
    /// </summary>
    public bool? TaintedAtWrite { get; init; }

    /// <summary>Taint lineage as a JSON array, or null. The trust hook never invents lineage; it only checks the shape.</summary>
    public string? TaintSourcesJson { get; init; }

    /// <summary>Earlier seq this event refers to.</summary>
    public long? TargetSeq { get; init; }

    /// <summary>Seq this event replies to.</summary>
    public long? ReplyToSeq { get; init; }

    /// <summary>The message a <c>message_edit</c> event changes. Required for that kind, unused otherwise.</summary>
    public Guid? MessageId { get; init; }

    /// <summary>The version a <c>message_edit</c> event creates (2 or more). Required for that kind, unused otherwise.</summary>
    public int? Version { get; init; }

    /// <summary>
    /// The membership state this event writes (a join or a leave), or null. The writer sets its joined and left seqs to the
    /// event's own seq, so the caller's values are only a plan.
    /// </summary>
    public MembershipChange? Membership { get; init; }

    /// <summary>
    /// The head the caller planned against. When set and the head under the lock differs, the writer throws
    /// <see cref="GcStaleHeadException"/> and writes nothing. A repeat of an earlier key is still answered with the original.
    /// </summary>
    public long? ExpectedHeadSeq { get; init; }

    /// <summary>Kind-specific payload as JSON text.</summary>
    public string? PayloadJson { get; init; }

    /// <summary>
    /// The event's sealed payload, or null (the default). When set, the writer stores the ciphertext in <c>payload_ct</c> and the
    /// key id in <c>key_id</c>; both are set or both null. A repeat of an earlier key returns the original event with its
    /// original sealed bytes.
    /// </summary>
    public SealedPayload? Sealed { get; init; }

    /// <summary>The message body; required for kind <c>message</c>, not allowed otherwise.</summary>
    public MessageBody? Message { get; init; }

    /// <summary>Principals mentioned. Already validated against membership by the caller (doc 01 WP8).</summary>
    public IReadOnlyList<Guid> Mentions { get; init; } = [];

    /// <summary>
    /// The Soul version the author produced this event under (doc 07 "Versioning"), set by the server from
    /// <see cref="ISoulBindings"/>, never from client input. Only an agent-authored event carries one. The writer checks it
    /// with <see cref="ISoulBindings.IsValidStampAsync"/> before it writes, because the log has no foreign key to Souls.
    /// A repeat of an earlier key returns the original event with its original stamp.
    /// </summary>
    public SoulRef? Soul { get; init; }
}

/// <summary>The stored identity of an appended event.</summary>
/// <param name="GcId">The group chat.</param>
/// <param name="Seq">The event's gap-free seq.</param>
/// <param name="EventId">The event's id.</param>
/// <param name="Duplicate">True when the idempotency key had been used and this is the original event.</param>
/// <param name="CreatedAt">When the original event was written.</param>
/// <param name="MessageId">The message the event wrote or edited, or null for an event that carries no message. A repeat returns the original's.</param>
public sealed record AppendResult(Guid GcId, long Seq, Guid EventId, bool Duplicate, DateTimeOffset CreatedAt, Guid? MessageId = null);
