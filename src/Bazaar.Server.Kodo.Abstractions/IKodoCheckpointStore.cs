namespace Bazaar.Server.Kodo;

/// <summary>
/// Checkpoints and anchor marks on the stored journal (doc 46 sections 6.5, 6.5.2 and 7.10). A checkpoint covers one window of records
/// and is never changed after it is written, so its root outlives retention pruning of the records it covers. Every method may throw
/// when the database is unreachable; the caller keeps its state and tries again.
/// </summary>
public interface IKodoCheckpointStore
{
    /// <summary>Stores a checkpoint. Returns false when the same checkpoint (same last sequence and digest) is already stored.</summary>
    /// <exception cref="KodoJournalConflictException">A different checkpoint is stored for that last sequence, or the window overlaps one.</exception>
    ValueTask<bool> SaveCheckpointAsync(KodoCheckpoint checkpoint, CancellationToken cancellationToken);

    /// <summary>The checkpoint with the highest last sequence, or null while there is none.</summary>
    ValueTask<KodoCheckpoint?> GetLatestCheckpointAsync(CancellationToken cancellationToken);

    /// <summary>The first sequence still stored in the journal, or null while it is empty. A first window starts here, because older records may be pruned.</summary>
    ValueTask<long?> GetFirstStoredSequenceAsync(CancellationToken cancellationToken);

    /// <summary>Checkpoints that no sink has accepted yet, oldest first, at most <paramref name="limit"/>.</summary>
    ValueTask<IReadOnlyList<KodoCheckpoint>> ListUnanchoredAsync(int limit, CancellationToken cancellationToken);

    /// <summary>The newest checkpoints with their anchor marks, newest first, at most <paramref name="limit"/>.</summary>
    ValueTask<IReadOnlyList<KodoCheckpointEntry>> ListCheckpointsAsync(int limit, CancellationToken cancellationToken);

    /// <summary>Records that <paramref name="sinkId"/> accepted the anchor of the checkpoint ending at <paramref name="checkpointSequence"/>. Repeating it changes nothing.</summary>
    ValueTask RecordAnchorAsync(long checkpointSequence, string sinkId, DateTimeOffset anchoredAt, CancellationToken cancellationToken);

    /// <summary>
    /// A Merkle inclusion proof of the record at <paramref name="sequence"/> against the checkpoint whose window covers it (the first
    /// checkpoint at or after the record). The outcome says why there is none: not covered by a checkpoint, records pruned, or
    /// stored records that no longer match the checkpoint root.
    /// </summary>
    ValueTask<KodoCheckpointProof> ProofFromCheckpointAsync(long sequence, CancellationToken cancellationToken);

    /// <summary>
    /// Retention that never removes the records between the newest anchored checkpoint and the head (see <see cref="KodoPruneRule"/>).
    /// With no anchored checkpoint nothing is removed. Deletes records older than <paramref name="olderThan"/>; returns the number deleted.
    /// </summary>
    ValueTask<long> PruneAnchoredAsync(DateTimeOffset olderThan, CancellationToken cancellationToken);
}

/// <summary>A checkpoint with the sinks that accepted its anchor.</summary>
public sealed record KodoCheckpointEntry(KodoCheckpoint Checkpoint, IReadOnlyList<KodoAnchorMark> Anchors);

/// <summary>One sink's accepted anchor.</summary>
public sealed record KodoAnchorMark(string SinkId, DateTimeOffset AnchoredAt);

/// <summary>Why a checkpoint proof was or was not built.</summary>
public enum KodoCheckpointProofOutcome
{
    /// <summary>No checkpoint covers the record yet (it is newer than the last checkpoint). The caller may fall back to the whole-journal proof.</summary>
    NotCheckpointed,

    /// <summary>The proof is in <see cref="KodoCheckpointProof.View"/>.</summary>
    Found,

    /// <summary>A checkpoint covers the record but retention removed records of its window, so inclusion can no longer be proven. The checkpoint root is kept.</summary>
    Pruned,

    /// <summary>The checkpoint is damaged (digest mismatch) or the stored records no longer hash to its root.</summary>
    Inconsistent,
}

/// <summary>Outcome of a checkpoint proof, with the proof when found.</summary>
public sealed record KodoCheckpointProof(KodoCheckpointProofOutcome Outcome, KodoJournalProofView? View = null);
