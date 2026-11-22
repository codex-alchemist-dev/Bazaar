using Bazaar.Contracts.Kodo;

namespace Bazaar.Server.Kodo;

/// <summary>
/// The read side of the Kodo server module (doc 46 sections 5.4 and 15.1): the latest status snapshot and the journal head.
/// Other modules and the HTTP surface read through this seam and never reach into the monitor.
/// </summary>
public interface IKodoStatusSource
{
    /// <summary>True when the module is switched on. A disabled module has no snapshot and runs nothing.</summary>
    bool Enabled { get; }

    /// <summary>The latest snapshot, or null before the first evaluation has completed.</summary>
    KodoStatusSnapshotDto? Latest { get; }

    /// <summary>The newest journal entries, newest first, at most <paramref name="limit"/>. Entries carry hashes and a redacted summary, never payload bytes.</summary>
    IReadOnlyList<KodoJournalEntryView> RecentJournal(int limit);

    /// <summary>A Merkle inclusion proof for the journal record at <paramref name="sequence"/>, or null when it is unknown or the retained history no longer starts at the genesis record.</summary>
    KodoJournalProofView? JournalProof(long sequence);
}

/// <summary>One journal entry as the read API shows it. Hashes are lowercase hex.</summary>
/// <param name="Sequence">The position in the chain, from 0.</param>
/// <param name="At">When it was recorded, in UTC.</param>
/// <param name="Kind">The record kind: <c>start</c>, <c>observation</c> or <c>state</c>.</param>
/// <param name="Hash">The record hash.</param>
/// <param name="PreviousHash">The previous record hash.</param>
/// <param name="Summary">A short redacted description built from probe ids, wire statuses and cause codes only.</param>
public sealed record KodoJournalEntryView(long Sequence, DateTimeOffset At, string Kind, string Hash, string PreviousHash, string Summary);

/// <summary>A Merkle inclusion proof (doc 46 section 6.5.1) over the journal record hashes. Hashes are lowercase hex.</summary>
/// <param name="Sequence">The record the proof is for.</param>
/// <param name="TreeSize">The number of records the root covers.</param>
/// <param name="RecordHash">The hash of the record.</param>
/// <param name="Root">The Merkle root over the first <paramref name="TreeSize"/> records.</param>
/// <param name="Path">The sibling hashes from the leaf to the root.</param>
public sealed record KodoJournalProofView(long Sequence, long TreeSize, string RecordHash, string Root, IReadOnlyList<string> Path);
