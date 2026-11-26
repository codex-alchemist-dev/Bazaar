using Bazaar.Contracts.Wire;
using Bazaar.Server.GcReactions.Abstractions;

namespace Bazaar.Server.GcReactions;

/// <summary>
/// Derives the current reactions from the log (docs/design/01, "Current state is derived"): per
/// <c>(author, target, emoji)</c> the latest event wins, and an <c>add</c> not followed by a <c>remove</c> is in effect.
/// </summary>
public static class ReactionFold
{
    /// <summary>The groups in effect. Rows may arrive in any order; they are applied by seq.</summary>
    public static IReadOnlyList<WireReactionGroup> Fold(IEnumerable<ReactionEventRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var groups = new Dictionary<(long Target, string Emoji), Group>();
        foreach (var row in rows.OrderBy(r => r.Seq))
        {
            var key = (row.TargetN, row.Emoji);
            if (row.Add)
            {
                if (!groups.TryGetValue(key, out var group))
                    groups[key] = group = new Group(row.Seq);
                if (group.By.All(b => b.Id != row.AuthorId))
                    group.By.Add((row.AuthorId, row.Seq));
            }
            else if (groups.TryGetValue(key, out var existing))
            {
                existing.By.RemoveAll(b => b.Id == row.AuthorId);
                if (existing.By.Count == 0)
                    groups.Remove(key);
            }
        }

        return [.. groups
            .OrderBy(g => g.Key.Target)
            .ThenBy(g => g.Value.FirstSeq)
            .Select(g => new WireReactionGroup(g.Key.Target, g.Key.Emoji, g.Value.By.Select(b => b.Id).ToList()))];
    }

    /// <summary>
    /// The seq of the event that put <paramref name="author"/>'s reaction in effect, or null when it is not in effect. For one
    /// message's rows (or any rows; only the matching triple counts).
    /// </summary>
    public static long? InEffectSeq(IEnumerable<ReactionEventRow> rows, Guid author, long target, string emoji)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var last = rows
            .Where(r => r.AuthorId == author && r.TargetN == target && r.Emoji == emoji)
            .OrderBy(r => r.Seq)
            .LastOrDefault();
        return last is { Add: true } ? last.Seq : null;
    }

    private sealed class Group(long firstSeq)
    {
        public long FirstSeq { get; } = firstSeq;

        public List<(Guid Id, long Seq)> By { get; } = [];
    }
}
