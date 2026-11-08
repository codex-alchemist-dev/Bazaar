namespace Bazaar.Server.Vault.Search;

/// <summary>A key with its fused score, in rank order.</summary>
public sealed record FusedRank<TKey>(TKey Key, double Score) where TKey : notnull;

/// <summary>
/// Reciprocal Rank Fusion: each list contributes 1 / (k + rank) for every key it holds, with rank counted from 1.
/// Ties keep the order in which keys were first seen, so the result is deterministic.
/// </summary>
public static class ReciprocalRankFusion
{
    public static IReadOnlyList<FusedRank<TKey>> Fuse<TKey>(IReadOnlyList<IReadOnlyList<TKey>> rankedLists, int k)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(rankedLists);
        if (k <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(k), k, "The RRF constant must be positive.");
        }

        var scores = new Dictionary<TKey, double>();
        var firstSeen = new List<TKey>();
        foreach (var list in rankedLists)
        {
            for (var index = 0; index < list.Count; index++)
            {
                var key = list[index];
                if (!scores.TryGetValue(key, out var score))
                {
                    firstSeen.Add(key);
                    score = 0;
                }

                scores[key] = score + 1.0 / (k + index + 1);
            }
        }

        return firstSeen
            .Select((key, order) => (key, order, score: scores[key]))
            .OrderByDescending(entry => entry.score)
            .ThenBy(entry => entry.order)
            .Select(entry => new FusedRank<TKey>(entry.key, entry.score))
            .ToList();
    }
}
