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

    /// <summary>
    /// Like <see cref="JournalProof"/>, and also consults the durable journal store when the memory window no longer holds the record
    /// (doc 46 section 7.10). The default answers from <see cref="JournalProof"/>.
    /// </summary>
    ValueTask<KodoJournalProofView?> JournalProofAsync(long sequence, CancellationToken cancellationToken) =>
        ValueTask.FromResult(JournalProof(sequence));

    /// <summary>
    /// One page of the displayed-state history, newest first (doc 46 section 7.10): at most <paramref name="limit"/> changes with a
    /// sequence below <paramref name="beforeSequence"/>, or from the newest when it is null. Reads the durable store when one is
    /// wired and falls back to the state records in the memory journal. The default has no history.
    /// </summary>
    ValueTask<IReadOnlyList<KodoStateChange>> StateHistoryAsync(long? beforeSequence, int limit, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<KodoStateChange>>([]);

    /// <summary>
    /// The proof route's lookup (doc 46 section 6.5.2): the checkpoint that covers the record first, then <see cref="JournalProofAsync"/>.
    /// The outcome tells a record whose window was pruned apart from an unknown one. The default answers from <see cref="JournalProofAsync"/>.
    /// </summary>
    async ValueTask<KodoProofLookup> LookupJournalProofAsync(long sequence, CancellationToken cancellationToken) =>
        await JournalProofAsync(sequence, cancellationToken).ConfigureAwait(false) is { } view
            ? new KodoProofLookup(KodoProofOutcome.Found, view)
            : new KodoProofLookup(KodoProofOutcome.NotAvailable);

    /// <summary>The newest checkpoints with their anchor marks, newest first. Empty when checkpoints are off. The default is empty.</summary>
    ValueTask<IReadOnlyList<KodoCheckpointView>> JournalAnchorsAsync(int limit, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<KodoCheckpointView>>([]);
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
/// <param name="WindowStart">For a checkpoint proof, the sequence of the first record in the checkpoint window: the leaf index is <c>Sequence - WindowStart</c>. Null for a whole-journal proof, where the leaf index is the sequence.</param>
/// <param name="CheckpointSequence">For a checkpoint proof, the last sequence of the checkpoint (its identity). Null for a whole-journal proof.</param>
public sealed record KodoJournalProofView(long Sequence, long TreeSize, string RecordHash, string Root, IReadOnlyList<string> Path, long? WindowStart = null, long? CheckpointSequence = null);

/// <summary>Why a proof lookup answered as it did.</summary>
public enum KodoProofOutcome
{
    /// <summary>The proof is in <see cref="KodoProofLookup.View"/>.</summary>
    Found,

    /// <summary>No proof is available (unknown record, or no checkpoint and no full history).</summary>
    NotAvailable,

    /// <summary>A checkpoint covers the record, but retention removed records of its window. Inclusion cannot be proven; the checkpoint root is kept.</summary>
    Pruned,

    /// <summary>The stored records or the checkpoint are damaged: the records no longer hash to the checkpoint root.</summary>
    Inconsistent,
}

/// <summary>A proof lookup result.</summary>
public sealed record KodoProofLookup(KodoProofOutcome Outcome, KodoJournalProofView? View = null);

/// <summary>One checkpoint as the read API shows it. Hashes are lowercase hex.</summary>
public sealed record KodoCheckpointView(
    long FirstSequence,
    long LastSequence,
    long RecordCount,
    DateTimeOffset FirstAt,
    DateTimeOffset LastAt,
    DateTimeOffset CutAt,
    string HeadHash,
    string Root,
    string Digest,
    bool DigestValid,
    bool HasSignature,
    string? SignerId,
    IReadOnlyList<KodoAnchorMark> Anchors);
