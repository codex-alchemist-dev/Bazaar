namespace Bazaar.Server.Agents.Inbox;

/// <summary>Groups digest items by GC into <see cref="DigestRange"/> values.</summary>
public static class DigestBatcher
{
    /// <summary>
    /// Groups the <see cref="InboxKind.GcDigest"/> items that have a GC id, one range per GC, ordered by GC id.
    /// Other kinds and items without a GC id are ignored.
    /// </summary>
    public static IReadOnlyList<DigestRange> Batch(IEnumerable<InboxCandidate> digestItems)
    {
        ArgumentNullException.ThrowIfNull(digestItems);

        return digestItems
            .Where(item => item.Kind == InboxKind.GcDigest && item.GcId is not null)
            .GroupBy(item => item.GcId!.Value)
            .OrderBy(group => group.Key)
            .Select(group => new DigestRange(
                group.Key,
                group.Min(item => item.GcSeq ?? 0),
                group.Max(item => item.GcSeq ?? 0),
                group.Count()))
            .ToList();
    }
}
