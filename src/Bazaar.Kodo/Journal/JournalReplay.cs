namespace Bazaar.Kodo.Journal;

/// <summary>
/// Idempotent replay of journal records (doc 46 section 7.5): when Postgres returns, spilled records
/// are applied by hash, so a record that is already stored is skipped.
/// </summary>
public static class JournalReplay
{
    /// <summary>
    /// Returns the records whose hash is not yet in <paramref name="appliedHashes"/>, in input order,
    /// and adds their hashes to that set. A record repeated within the input is applied once.
    /// Replaying the same input again returns nothing.
    /// </summary>
    public static IReadOnlyList<JournalRecord> Apply(IEnumerable<JournalRecord> records, ISet<string> appliedHashes)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(appliedHashes);

        List<JournalRecord> applied = [];
        foreach (JournalRecord record in records)
        {
            if (appliedHashes.Add(JournalChain.KeyOf(record)))
            {
                applied.Add(record);
            }
        }

        return applied;
    }
}
