using Bazaar.Kodo.Journal;

namespace Bazaar.Server.Kodo.Postgres;

/// <summary>
/// Verifies a run of journal records that need not start at the genesis record (a range read, or a store pruned by retention).
/// Pure logic: no I/O. <see cref="JournalChain.Verify"/> covers only whole chains from record 0.
/// </summary>
internal static class KodoJournalRangeVerifier
{
    /// <summary>
    /// The index of the first record that does not verify, or null when the run is intact. Records must be consecutive, each must
    /// link to its predecessor and each hash must match its bytes. When the run starts at sequence 0 the first record must link to
    /// the genesis hash.
    /// </summary>
    public static int? FirstBroken(IReadOnlyList<JournalRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        for (int index = 0; index < records.Count; index++)
        {
            JournalRecord record = records[index];
            if (record.PreviousHash.Length != JournalChain.HashLength || record.Hash.Length != JournalChain.HashLength)
            {
                return index;
            }

            if (index == 0)
            {
                if (record.Sequence < 0 || (record.Sequence == 0 && !record.PreviousHash.AsSpan().SequenceEqual(JournalChain.GenesisHash())))
                {
                    return 0;
                }
            }
            else if (record.Sequence != records[index - 1].Sequence + 1 || !record.PreviousHash.AsSpan().SequenceEqual(records[index - 1].Hash))
            {
                return index;
            }

            if (!HashMatches(record))
            {
                return index;
            }
        }

        return null;
    }

    private static bool HashMatches(JournalRecord record)
    {
        try
        {
            return record.Hash.AsSpan().SequenceEqual(JournalChain.ComputeHash(record.PreviousHash, JournalCodec.Encode(record)));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
