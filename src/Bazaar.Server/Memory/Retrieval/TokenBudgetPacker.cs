namespace Bazaar.Server.Memory.Retrieval;

/// <summary>
/// Packs ranked hits into a token budget (doc 08, section 8, step 3). Hits are walked in rank order and kept while
/// they fit. A hit that does not fit is skipped and the walk continues, so smaller later hits can still use the
/// remaining budget. Pure: the caller supplies the token count of every candidate.
/// </summary>
public static class TokenBudgetPacker
{
    /// <summary>
    /// Returns the kept hits in their input (rank) order. The total of their token counts never exceeds
    /// <paramref name="tokenBudget"/>.
    /// </summary>
    /// <exception cref="ArgumentException">A hit has no token count, or a count is negative.</exception>
    public static IReadOnlyList<FusedHit> Pack(
        IReadOnlyList<FusedHit> hits,
        IReadOnlyDictionary<string, int> tokenCounts,
        int tokenBudget)
    {
        ArgumentNullException.ThrowIfNull(hits);
        ArgumentNullException.ThrowIfNull(tokenCounts);
        ArgumentOutOfRangeException.ThrowIfNegative(tokenBudget);

        var packed = new List<FusedHit>();
        long used = 0;

        foreach (FusedHit hit in hits)
        {
            if (!tokenCounts.TryGetValue(hit.Id, out int tokens))
            {
                throw new ArgumentException($"No token count was supplied for candidate '{hit.Id}'.", nameof(tokenCounts));
            }

            if (tokens < 0)
            {
                throw new ArgumentException($"Token count for candidate '{hit.Id}' is negative.", nameof(tokenCounts));
            }

            if (used + tokens > tokenBudget)
            {
                continue;
            }

            used += tokens;
            packed.Add(hit);
        }

        return packed;
    }
}
