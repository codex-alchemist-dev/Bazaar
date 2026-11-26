using Bazaar.Kodo.Journal;

namespace Bazaar.Server.Kodo.Postgres;

/// <summary>What <see cref="KodoJournalAppendPlanner.Plan"/> decided.</summary>
/// <param name="ToInsert">The records to write, in order.</param>
/// <param name="Skipped">The count of records already stored, by hash.</param>
/// <param name="Conflict">Why the records were refused, or null when the plan is good.</param>
/// <param name="Message">A message for the conflict, without payload bytes.</param>
internal sealed record KodoJournalAppendPlan(IReadOnlyList<JournalRecord> ToInsert, int Skipped, KodoJournalConflict? Conflict, string? Message);

/// <summary>
/// The idempotent replay rule of doc 46 section 7.5, as pure logic. Given the stored head and the stored hashes for the sequences the
/// incoming records cover, it decides which records to write. A record already stored under the same hash is skipped
/// (<see cref="JournalReplay.Apply"/>), a record whose sequence holds another hash is a fork, and the first new record must continue
/// the stored head. An empty store accepts any start, because the local memory window may have dropped older records (a gap is
/// never filled with invented data).
/// </summary>
internal static class KodoJournalAppendPlanner
{
    /// <param name="head">The stored head, or null for an empty store.</param>
    /// <param name="storedHashes">Stored record hashes by sequence, for at least the sequences of <paramref name="incoming"/> that exist in the store.</param>
    /// <param name="incoming">The records to store.</param>
    public static KodoJournalAppendPlan Plan(JournalRecord? head, IReadOnlyDictionary<long, byte[]> storedHashes, IReadOnlyList<JournalRecord> incoming)
    {
        ArgumentNullException.ThrowIfNull(storedHashes);
        ArgumentNullException.ThrowIfNull(incoming);

        if (incoming.Count == 0)
        {
            return new KodoJournalAppendPlan([], 0, null, null);
        }

        if (KodoJournalRangeVerifier.FirstBroken(incoming) is int broken)
        {
            return Refuse(KodoJournalConflict.BrokenChain, $"Incoming journal record {incoming[broken].Sequence} does not verify.");
        }

        HashSet<string> applied = [];
        foreach (JournalRecord record in incoming)
        {
            if (storedHashes.TryGetValue(record.Sequence, out byte[]? stored))
            {
                if (!stored.AsSpan().SequenceEqual(record.Hash))
                {
                    return Refuse(KodoJournalConflict.Diverged, $"Stored journal record {record.Sequence} has another hash.");
                }

                applied.Add(JournalChain.KeyOf(record));
            }
        }

        IReadOnlyList<JournalRecord> fresh = JournalReplay.Apply(incoming, applied);
        if (fresh.Count == 0)
        {
            return new KodoJournalAppendPlan([], incoming.Count, null, null);
        }

        // Records are consecutive, so "stored" ones form a prefix of the run; the fresh ones follow it.
        JournalRecord first = fresh[0];
        if (head is not null)
        {
            if (first.Sequence > head.Sequence + 1)
            {
                return Refuse(KodoJournalConflict.Gap, $"Journal records {head.Sequence + 1} to {first.Sequence - 1} are missing.");
            }

            if (first.Sequence != head.Sequence + 1 || !first.PreviousHash.AsSpan().SequenceEqual(head.Hash))
            {
                return Refuse(KodoJournalConflict.Diverged, $"Journal record {first.Sequence} does not continue the stored head.");
            }
        }

        return new KodoJournalAppendPlan(fresh, incoming.Count - fresh.Count, null, null);
    }

    private static KodoJournalAppendPlan Refuse(KodoJournalConflict conflict, string message) => new([], 0, conflict, message);
}
