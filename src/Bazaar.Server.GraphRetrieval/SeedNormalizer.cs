namespace Bazaar.Server.GraphRetrieval;

internal static class SeedNormalizer
{
    /// <summary>
    /// Validates seeds (finite, non-negative scores; nodes inside the graph), drops masked seeds, merges duplicates by
    /// keeping the largest score, and returns them ordered by node. Returns false for malformed input.
    /// </summary>
    public static bool TryNormalize(GraphSnapshot g, IReadOnlyList<SeedScore> seeds, NodeMask? mask, out SeedScore[] result)
    {
        result = [];
        var best = new SortedDictionary<int, double>();
        foreach (SeedScore s in seeds)
        {
            if ((uint)s.Node >= (uint)g.NodeCount || !double.IsFinite(s.Score) || s.Score < 0)
            {
                return false;
            }

            if (mask is not null && !mask.Contains(s.Node))
            {
                continue;
            }

            best[s.Node] = best.TryGetValue(s.Node, out double old) ? Math.Max(old, s.Score) : s.Score;
        }

        result = [.. best.Select(static kv => new SeedScore(kv.Key, kv.Value))];
        return true;
    }

    public static bool MaskMatches(GraphSnapshot g, NodeMask? mask) => mask is null || mask.Length == g.NodeCount;
}
