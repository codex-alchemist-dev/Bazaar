namespace Bazaar.Server.Agents.Inbox;

/// <summary>
/// The result of planning one wake. <see cref="Primary"/> is the item that starts the turn; it is null when only digest
/// items are due. Other non-digest items are not in the batch and stay pending.
/// </summary>
/// <param name="Primary">The highest-priority due item that is not a digest item, or null.</param>
/// <param name="Digest">The due digest items, grouped by GC.</param>
public sealed record WakeBatch(InboxCandidate? Primary, IReadOnlyList<DigestRange> Digest);

/// <summary>Orders due inbox items by wake priority and plans a wake batch from them. Pure: no storage or clock.</summary>
public static class InboxSelector
{
    /// <summary>
    /// The items due at <paramref name="now"/>, ordered by <see cref="InboxPriority.Rank"/>, then due time, then GC
    /// sequence (nulls last), then id. Items due after <paramref name="now"/> are excluded.
    /// </summary>
    /// <exception cref="ArgumentException">An item has an unknown kind.</exception>
    public static IReadOnlyList<InboxCandidate> Select(IEnumerable<InboxCandidate> pending, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(pending);

        return pending
            .Where(item => item.DueAt <= now)
            .OrderBy(item => InboxPriority.Rank(item.Kind))
            .ThenBy(item => item.DueAt)
            .ThenBy(item => item.GcSeq.HasValue ? 0 : 1)
            .ThenBy(item => item.GcSeq ?? 0)
            .ThenBy(item => item.Id)
            .ToList();
    }

    /// <summary>
    /// Plans one wake. The primary is the first selected non-digest item. Every due digest item is folded into
    /// <see cref="WakeBatch.Digest"/>, grouped by GC. Digest items are never chosen as the primary.
    /// </summary>
    public static WakeBatch Plan(IEnumerable<InboxCandidate> pending, DateTimeOffset now)
    {
        var selected = Select(pending, now);

        var primary = selected.FirstOrDefault(item => item.Kind != InboxKind.GcDigest);
        var digest = DigestBatcher.Batch(selected);

        return new WakeBatch(primary, digest);
    }
}
