namespace Bazaar.Server.Council.Prompts;

/// <summary>
/// Reason order for the reveal (council.reason_order = random; docs/design/10-council-and-voting.md, Anti-sycophancy measures).
/// A seeded Fisher-Yates shuffle: the same seed and count always give the same order, so the seed stored on the vote
/// reproduces it. Aliases are assigned by position in that order.
/// </summary>
public static class ReasonOrder
{
    /// <summary>
    /// The permutation of indices 0 to count-1 for the seed. Position i of the result is the source index shown at position i.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    public static IReadOnlyList<int> Permutation(int count, long seed)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        var order = Enumerable.Range(0, count).ToArray();
        var random = new Random(FoldSeed(seed));

        // Fisher-Yates: walk from the end, swapping each slot with a uniformly chosen slot at or before it.
        for (var i = count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }

        return order;
    }

    /// <summary>Returns the items in the seeded order. The input is not changed.</summary>
    public static IReadOnlyList<T> Shuffle<T>(IReadOnlyList<T> items, long seed)
    {
        ArgumentNullException.ThrowIfNull(items);

        var permutation = Permutation(items.Count, seed);
        return permutation.Select(index => items[index]).ToArray();
    }

    /// <summary>
    /// Assigns aliases by position: index 0 is "Reason A", 25 is "Reason Z", 26 is "Reason AA", and so on. Pure: the same
    /// position always gets the same alias.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is negative.</exception>
    public static string AliasAt(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);

        var letters = new Stack<char>();
        var n = index + 1;
        while (n > 0)
        {
            n--;
            letters.Push((char)('A' + (n % 26)));
            n /= 26;
        }

        return "Reason " + new string(letters.ToArray());
    }

    /// <summary>
    /// Labels reasons that are already in the stored order. The caller shuffles first, then calls this; the output carries
    /// no voter ids.
    /// </summary>
    public static IReadOnlyList<RevealedReason> AssignAliases(IReadOnlyList<string> reasoningsInOrder)
    {
        ArgumentNullException.ThrowIfNull(reasoningsInOrder);

        return reasoningsInOrder
            .Select((reasoning, index) => new RevealedReason(AliasAt(index), reasoning))
            .ToArray();
    }

    /// <summary>
    /// Folds the 64-bit seed into the 32-bit seed System.Random takes. Deterministic.
    /// </summary>
    private static int FoldSeed(long seed)
    {
        var bits = unchecked((ulong)seed);
        return unchecked((int)(bits ^ (bits >> 32)));
    }
}
