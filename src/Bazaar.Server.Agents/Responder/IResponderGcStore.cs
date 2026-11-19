using Bazaar.Contracts.Gc;

namespace Bazaar.Server.Agents.Responder;

/// <summary>One message as the responder sees it: its current text, decoded, and who wrote it.</summary>
/// <param name="Seq">Seq of the message's first event. Replies cite it as the trigger.</param>
/// <param name="MessageId">The message id; the reply's idempotency key is derived from it.</param>
/// <param name="AuthorKind">Who wrote the message.</param>
/// <param name="AuthorId">The author's principal id.</param>
/// <param name="Text">The current text of the message (the latest version).</param>
/// <param name="Mentions">Principal ids the message mentions.</param>
public sealed record ResponderMessage(
    long Seq,
    Guid MessageId,
    AuthorKind AuthorKind,
    Guid AuthorId,
    string Text,
    IReadOnlyList<Guid> Mentions);

/// <summary>The GC facts a reply decision needs.</summary>
/// <param name="Archived">An archived GC takes no writes.</param>
/// <param name="ActiveMembers">Principals with an active membership row (no <c>left_seq</c>).</param>
public sealed record ResponderGcSnapshot(bool Archived, IReadOnlySet<Guid> ActiveMembers);

/// <summary>
/// What the responder reads from and checks against a GC. The Postgres implementation is <see cref="PgResponderGcStore"/>.
/// The responder never reads a GC it has no membership in: the membership check comes first, so this store is not a viewer rule.
/// </summary>
public interface IResponderGcStore
{
    /// <summary>The GC's archive state and active members, or null when the GC does not exist.</summary>
    Task<ResponderGcSnapshot?> GetSnapshotAsync(Guid gcId, CancellationToken ct);

    /// <summary>Messages whose first event lies in (<paramref name="afterSeq"/>, <paramref name="throughSeq"/>], oldest first.</summary>
    Task<IReadOnlyList<ResponderMessage>> ReadNewMessagesAsync(Guid gcId, long afterSeq, long throughSeq, int limit, CancellationToken ct);

    /// <summary>The newest <paramref name="limit"/> messages up to <paramref name="throughSeq"/>, oldest first. Quarantined messages are left out.</summary>
    Task<IReadOnlyList<ResponderMessage>> ReadHistoryAsync(Guid gcId, long throughSeq, int limit, CancellationToken ct);

    /// <summary>True when an event with this author and client message id already exists in the GC.</summary>
    Task<bool> HasEventAsync(Guid gcId, Guid authorId, string clientMsgId, CancellationToken ct);
}
