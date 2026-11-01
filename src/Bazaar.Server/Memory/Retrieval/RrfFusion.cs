namespace Bazaar.Server.Memory.Retrieval;

/// <summary>
/// Reciprocal rank fusion over a lexical list and a vector list (doc 08, section 8). Pure: no I/O, no embeddings.
/// Each list contributes 1 / (k + rank) for every id it holds, with rank starting at 1. A duplicate id inside one
/// list counts only at its first (best) rank. Results are sorted by score descending, then by id ordinal ascending,
/// so equal scores always come out in the same order.
/// </summary>
public static class RrfFusion
{
    /// <summary>
    /// Fuses the two ranked lists and returns at most <see cref="RetrievalOptions.TopK"/> hits.
    /// An empty vector list marks every result <see cref="FusedHit.LexicalOnly"/>.
    /// </summary>
    public static IReadOnlyList<FusedHit> Fuse(
        IReadOnlyList<RankedHit> lexical,
        IReadOnlyList<RankedHit> vector,
        RetrievalOptions options)
    {
        ArgumentNullException.ThrowIfNull(lexical);
        ArgumentNullException.ThrowIfNull(vector);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.RrfK, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(options.TopK);

        var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);

        // Lexical first, then vector, so the order of additions is fixed.
        AddList(entries, lexical, options.RrfK, isLexical: true);
        AddList(entries, vector, options.RrfK, isLexical: false);

        bool lexicalOnly = vector.Count == 0;

        return entries
            .Select(pair => new FusedHit(
                pair.Key,
                pair.Value.Score,
                pair.Value.FromLexical,
                pair.Value.FromVector,
                lexicalOnly))
            .OrderByDescending(hit => hit.Score)
            .ThenBy(hit => hit.Id, StringComparer.Ordinal)
            .Take(options.TopK)
            .ToArray();
    }

    private static void AddList(
        Dictionary<string, Entry> entries,
        IReadOnlyList<RankedHit> list,
        int k,
        bool isLexical)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < list.Count; i++)
        {
            string id = list[i].Id;
            ArgumentException.ThrowIfNullOrEmpty(id);

            if (!seen.Add(id))
            {
                continue;
            }

            double contribution = 1.0 / (k + (i + 1));
            Entry entry = entries.GetValueOrDefault(id);

            entries[id] = isLexical
                ? entry with { Score = entry.Score + contribution, FromLexical = true }
                : entry with { Score = entry.Score + contribution, FromVector = true };
        }
    }

    private readonly record struct Entry(double Score, bool FromLexical, bool FromVector);
}
