namespace Bazaar.Server.GcReactions.Abstractions;

/// <summary>One <c>reaction</c> event of a GC log, decoded (docs/design/01, "Reactions").</summary>
/// <param name="Seq">The event's gap-free seq.</param>
/// <param name="AuthorId">Who reacted or removed.</param>
/// <param name="TargetN">The target message's first seq.</param>
/// <param name="Emoji">The emoji.</param>
/// <param name="Add">True for <c>add</c>, false for <c>remove</c>.</param>
public sealed record ReactionEventRow(long Seq, Guid AuthorId, long TargetN, string Emoji, bool Add);

/// <summary>The read side of reactions over the GC log. Reactions are written through the GC log writer, never here.</summary>
public interface IGcReactionStore
{
    /// <summary>True when the GC has a <c>message</c> event at <paramref name="n"/>.</summary>
    Task<bool> MessageExistsAsync(Guid gcId, long n, CancellationToken ct);

    /// <summary>
    /// The GC's reaction events in seq order. <paramref name="targetN"/> limits them to one message; <paramref name="afterN"/> to
    /// messages with <c>n</c> above it. Both null reads every reaction of the GC.
    /// </summary>
    Task<IReadOnlyList<ReactionEventRow>> ReadAsync(Guid gcId, long? targetN, long? afterN, CancellationToken ct);
}
