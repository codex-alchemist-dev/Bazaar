using Bazaar.Kodo.Journal;

namespace Bazaar.Server.Kodo;

/// <summary>
/// The durable store behind the Kodo journal (doc 46 sections 6.5, 7.5 and 7.10). The local chain (memory and the spill file) stays the
/// writer; the store is the database copy that the local records are replayed into, by record hash, so replaying twice stores each
/// record once. Every method may throw when the database is unreachable; the caller keeps the records locally and tries again.
/// </summary>
public interface IKodoJournalStore
{
    /// <summary>The newest stored record, or null while the store is empty.</summary>
    ValueTask<JournalRecord?> GetHeadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Stores consecutive records that continue the stored chain. A record whose hash is already stored is skipped. Record kind
    /// <c>state</c> also writes the state-change history, in the same transaction.
    /// </summary>
    /// <exception cref="KodoJournalConflictException">The records are not linked, do not continue the stored head, or differ from a stored record at the same sequence.</exception>
    ValueTask<KodoJournalAppendResult> AppendAsync(IReadOnlyList<JournalRecord> records, CancellationToken cancellationToken);

    /// <summary>
    /// Reads up to <paramref name="limit"/> records from <paramref name="fromSequence"/> upward. The chain links of the range are
    /// verified; a break throws <see cref="KodoJournalConflictException"/> with <see cref="KodoJournalConflict.BrokenChain"/>.
    /// </summary>
    ValueTask<IReadOnlyList<JournalRecord>> ReadRangeAsync(long fromSequence, int limit, CancellationToken cancellationToken);

    /// <summary>A Merkle inclusion proof for <paramref name="sequence"/> over the whole stored history, or null when it is unknown or the history no longer starts at record 0.</summary>
    ValueTask<KodoJournalProofView?> ProofAsync(long sequence, CancellationToken cancellationToken);

    /// <summary>Deletes records older than <paramref name="olderThan"/>, always keeping the head. Returns the number deleted.</summary>
    ValueTask<long> PruneAsync(DateTimeOffset olderThan, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the latest status snapshot (wire JSON bytes). The last write wins: stream revisions restart with the process, so a
    /// stored revision cannot order snapshots across restarts.
    /// </summary>
    ValueTask SaveSnapshotAsync(long revision, byte[] snapshotJson, DateTimeOffset savedAt, CancellationToken cancellationToken);

    /// <summary>The latest stored snapshot, or null.</summary>
    ValueTask<KodoStoredSnapshot?> GetSnapshotAsync(CancellationToken cancellationToken);

    /// <summary>The newest state changes, newest first.</summary>
    ValueTask<IReadOnlyList<KodoStateChange>> RecentStateChangesAsync(int limit, CancellationToken cancellationToken);

    /// <summary>
    /// One page of the state history for <c>GET /history</c> (doc 46 section 7.10): up to <paramref name="limit"/> changes, newest
    /// first, with a sequence below <paramref name="beforeSequence"/> (or from the newest when it is null).
    /// </summary>
    ValueTask<IReadOnlyList<KodoStateChange>> ReadStateChangesAsync(long? beforeSequence, int limit, CancellationToken cancellationToken);
}

/// <summary>What one <see cref="IKodoJournalStore.AppendAsync"/> call did.</summary>
/// <param name="Stored">Records newly written.</param>
/// <param name="Skipped">Records already stored, by hash.</param>
public readonly record struct KodoJournalAppendResult(int Stored, int Skipped);

/// <summary>One displayed-state change from the journal, for the state-history view.</summary>
/// <param name="Sequence">The journal record that carries the change.</param>
/// <param name="At">When it was recorded, in UTC.</param>
/// <param name="From">The wire name of the previous state.</param>
/// <param name="To">The wire name of the new state.</param>
public sealed record KodoStateChange(long Sequence, DateTimeOffset At, string From, string To);

/// <summary>The latest stored status snapshot.</summary>
/// <param name="Revision">The stream revision of the snapshot.</param>
/// <param name="Json">The wire JSON of the snapshot.</param>
/// <param name="SavedAt">When it was stored, in UTC.</param>
public sealed record KodoStoredSnapshot(long Revision, byte[] Json, DateTimeOffset SavedAt);

/// <summary>Why a store refused records.</summary>
public enum KodoJournalConflict
{
    /// <summary>The records are not consecutive, not linked, or a hash does not match its bytes.</summary>
    BrokenChain,

    /// <summary>The first new record starts after the stored head with records missing in between.</summary>
    Gap,

    /// <summary>A stored record at the same sequence has another hash, or the first new record does not link to the stored head: the chains forked.</summary>
    Diverged,
}

/// <summary>Raised by a store that refuses records. The message never carries payload bytes.</summary>
public sealed class KodoJournalConflictException(KodoJournalConflict conflict, string message) : InvalidOperationException(message)
{
    /// <summary>Why the records were refused.</summary>
    public KodoJournalConflict Conflict { get; } = conflict;
}
