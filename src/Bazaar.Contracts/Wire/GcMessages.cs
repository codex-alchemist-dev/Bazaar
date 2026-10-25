namespace Bazaar.Contracts.Wire;

/// <summary>
/// Event <c>gc.message</c>: a message as a stream shows it. <see cref="N"/> is the message's log entry;
/// <see cref="Ev"/> is the version counter, so an edit repeats the original <c>n</c> with a higher <c>ev</c>
/// (doc 02, sequence numbers).
/// </summary>
/// <param name="GcId">The group chat.</param>
/// <param name="N">The message's log sequence number.</param>
/// <param name="Ev">Per-GC counter of this message version.</param>
/// <param name="MessageId">Stable message id across edits.</param>
/// <param name="AuthorId">Principal who wrote the message.</param>
/// <param name="Text">Text of this version.</param>
/// <param name="Ts">When the server wrote this version.</param>
/// <param name="Edited">True when this is not the first version.</param>
/// <param name="Mentions">Principals mentioned.</param>
/// <param name="ReplyToN">Sequence number of the entry this replies to.</param>
public sealed record WireGcMessage(
    Guid GcId,
    long N,
    long Ev,
    Guid MessageId,
    Guid AuthorId,
    string Text,
    DateTimeOffset Ts,
    bool Edited,
    IReadOnlyList<Guid> Mentions,
    long? ReplyToN);

/// <summary>Request <c>gc.send</c>. The envelope <c>id</c> is the idempotency key.</summary>
/// <param name="GcId">The group chat.</param>
/// <param name="Text">Message text.</param>
/// <param name="Mentions">Principals mentioned.</param>
/// <param name="ReplyToN">Sequence number replied to.</param>
public sealed record GcSend(Guid GcId, string Text, IReadOnlyList<Guid>? Mentions, long? ReplyToN);

/// <summary>Response to <c>gc.send</c>.</summary>
/// <param name="N">The assigned log sequence number.</param>
/// <param name="Ev">The assigned version counter.</param>
public sealed record GcSendResult(long N, long Ev);

/// <summary>Request <c>gc.history</c>: older entries.</summary>
/// <param name="GcId">The group chat.</param>
/// <param name="Before">Return entries with <c>n</c> below this.</param>
/// <param name="Limit">Maximum entries.</param>
public sealed record GcHistory(Guid GcId, long Before, int Limit);

/// <summary>Response to <c>gc.history</c>.</summary>
/// <param name="Items">Entries, oldest first.</param>
/// <param name="HasMore">True when older entries remain.</param>
public sealed record GcPage(IReadOnlyList<WireGcMessage> Items, bool HasMore);

/// <summary>Request <c>gc.read</c>: mark read.</summary>
/// <param name="GcId">The group chat.</param>
/// <param name="UpToN">Read up to and including this <c>n</c>.</param>
public sealed record GcRead(Guid GcId, long UpToN);
