namespace Bazaar.Server.Council.Service;

/// <summary>
/// The order in which reasons appear at the reveal (docs/design/10-council-and-voting.md, Anti-sycophancy measures,
/// council.reason_order). Voters are sorted first, so the result does not depend on submit order. The random order is a
/// seeded Fisher-Yates shuffle with SplitMix64, so it is the same for the same seed on every runtime, and the service
/// also stores the order it used in the reveal event.
/// </summary>
internal static class RevealOrder
{
    /// <summary>Arranges the voter ids for the reveal.</summary>
    public static IReadOnlyList<string> Arrange(IEnumerable<string> voterIds, long seed, bool random)
    {
        var ordered = voterIds
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        if (!random)
        {
            return ordered;
        }

        var state = unchecked((ulong)seed);
        for (var i = ordered.Length - 1; i > 0; i--)
        {
            var j = (int)(NextUInt64(ref state) % (ulong)(i + 1));
            (ordered[i], ordered[j]) = (ordered[j], ordered[i]);
        }

        return ordered;
    }

    private static ulong NextUInt64(ref ulong state)
    {
        state += 0x9E3779B97F4A7C15UL;
        var z = state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
