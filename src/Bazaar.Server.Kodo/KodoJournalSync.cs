using Bazaar.Contracts.Kodo;
using Bazaar.Kodo.Api;
using Bazaar.Kodo.Journal;
using Bazaar.Kodo.Probes.Self;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Kodo;

/// <summary>
/// Replays the local journal (memory and spill) into the durable <see cref="IKodoJournalStore"/> (doc 46 sections 7.5 and 7.10). The
/// local chain stays the writer and never waits on the database: a failed call leaves the records where they are and the next pass
/// tries again, so an outage is just a growing backlog that <c>kodo.self.spill</c> reports by age. A replay is idempotent by record
/// hash. A chain that forks from the stored one, or a hole between them, is never merged: the sync stops and the probe goes red.
/// </summary>
internal sealed partial class KodoJournalSync
{
    /// <summary>Records sent per store call.</summary>
    internal const int BatchSize = 500;

    /// <summary>Least time between two snapshot writes.</summary>
    internal static readonly TimeSpan SnapshotInterval = TimeSpan.FromSeconds(30);

    /// <summary>Least time between two retention passes.</summary>
    internal static readonly TimeSpan PruneInterval = TimeSpan.FromHours(1);

    private readonly KodoJournal _journal;
    private readonly IKodoJournalStore _store;
    private readonly KodoServerOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly int _capacity;
    private long? _storedThrough;
    private bool _blocked;
    private bool _reachable = true;
    private DateTimeOffset _lastSnapshot = DateTimeOffset.MinValue;
    private DateTimeOffset _lastPrune = DateTimeOffset.MinValue;

    public KodoJournalSync(KodoJournal journal, IKodoJournalStore store, KodoServerOptions options, TimeProvider time, ILogger logger)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _capacity = options.JournalMemoryRecords;
    }

    /// <summary>True when the local and stored chains forked or have a hole, so nothing more is sent until an operator looks.</summary>
    public bool Blocked => _blocked;

    /// <summary>The sequence of the newest record the store holds, or null while that is not known.</summary>
    public long? StoredThrough => _storedThrough;

    /// <summary>
    /// What <c>kodo.self.spill</c> reads: the time of the oldest record not yet in the store, or null when none is waiting. A blocked
    /// sync reports sealed, which the probe treats as red.
    /// </summary>
    public SpillFileReading? ReadSpill()
    {
        IReadOnlyList<JournalRecord> waiting = _journal.ReadFrom((_storedThrough ?? -1) + 1, 1);
        if (_blocked)
        {
            DateTimeOffset since = waiting.Count > 0 ? waiting[0].At : _time.GetUtcNow();
            return new SpillFileReading(since, Sealed: true);
        }

        return waiting.Count == 0 ? null : new SpillFileReading(waiting[0].At, Sealed: false);
    }

    /// <summary>
    /// Connects to the store before the journal writes its first record: with no local records and a stored head, the chain continues
    /// from the store. Returns false when the store cannot be reached (the local journal carries on and a later pass connects).
    /// </summary>
    public async Task<bool> ConnectAsync(CancellationToken cancellationToken)
    {
        if (_blocked)
        {
            return false;
        }

        if (_storedThrough is not null)
        {
            return true;
        }

        try
        {
            JournalRecord? head = await _store.GetHeadAsync(cancellationToken).ConfigureAwait(false);
            if (head is null)
            {
                _storedThrough = -1;
            }
            else if (_journal.NextSequence == 0)
            {
                long from = Math.Max(0, head.Sequence - _capacity + 1);
                IReadOnlyList<JournalRecord> tail = await _store.ReadRangeAsync(from, _capacity, cancellationToken).ConfigureAwait(false);
                if (tail.Count == 0 || tail[^1].Sequence != head.Sequence || !_journal.AdoptTail(tail))
                {
                    return false;
                }

                _storedThrough = head.Sequence;
            }
            else if (head.Sequence >= _journal.NextSequence)
            {
                LogForked(_logger, head.Sequence, _journal.NextSequence);
                return Block();
            }
            else
            {
                IReadOnlyList<JournalRecord> local = _journal.ReadFrom(head.Sequence, 1);
                if (local.Count > 0 && local[0].Sequence == head.Sequence && !local[0].Hash.AsSpan().SequenceEqual(head.Hash))
                {
                    LogForked(_logger, head.Sequence, _journal.NextSequence);
                    return Block();
                }

                _storedThrough = head.Sequence;
            }

            MarkReachable();
            return true;
        }
        catch (KodoJournalConflictException error)
        {
            LogConflict(_logger, error.Conflict, error.Message);
            return Block();
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return Unreachable(error);
        }
    }

    /// <summary>One sync pass: replay what the store lacks, then (at most) save the snapshot and prune. Never throws except on the caller's cancellation.</summary>
    public async Task SyncAsync(KodoStatusSnapshotDto? latest, CancellationToken cancellationToken)
    {
        if (!await ConnectAsync(cancellationToken).ConfigureAwait(false) || _storedThrough is not long)
        {
            return;
        }

        try
        {
            while (true)
            {
                long next = _storedThrough.Value + 1;
                IReadOnlyList<JournalRecord> batch = _journal.ReadFrom(next, BatchSize);
                if (batch.Count == 0)
                {
                    break;
                }

                if (batch[0].Sequence > next && _storedThrough >= 0)
                {
                    // The memory window no longer holds the records in between. A gap is marked, never filled (doc 46 section 7.5).
                    LogGap(_logger, next, batch[0].Sequence - 1);
                    Block();
                    return;
                }

                await _store.AppendAsync(batch, cancellationToken).ConfigureAwait(false);
                _storedThrough = batch[^1].Sequence;
                MarkReachable();
            }

            await SaveSnapshotAsync(latest, cancellationToken).ConfigureAwait(false);
            await PruneAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (KodoJournalConflictException error)
        {
            LogConflict(_logger, error.Conflict, error.Message);
            Block();
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Reconnect (and re-read the stored head) on the next pass: a half-applied call is settled by the store's idempotence.
            _storedThrough = null;
            Unreachable(error);
        }
    }

    private async Task SaveSnapshotAsync(KodoStatusSnapshotDto? latest, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _time.GetUtcNow();
        if (latest is null || now - _lastSnapshot < SnapshotInterval)
        {
            return;
        }

        await _store.SaveSnapshotAsync(latest.Revision, KodoWireCodec.EncodeSnapshot(latest), now, cancellationToken).ConfigureAwait(false);
        _lastSnapshot = now;
    }

    private async Task PruneAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = _time.GetUtcNow();
        if (now - _lastPrune < PruneInterval)
        {
            return;
        }

        _lastPrune = now;
        long deleted = await _store.PruneAsync(now - TimeSpan.FromDays(_options.JournalRetentionDays), cancellationToken).ConfigureAwait(false);
        if (deleted > 0)
        {
            LogPruned(_logger, deleted);
        }
    }

    private bool Block()
    {
        _blocked = true;
        return false;
    }

    private bool Unreachable(Exception error)
    {
        if (_reachable)
        {
            _reachable = false;
            LogUnreachable(_logger, error);
        }

        return false;
    }

    private void MarkReachable()
    {
        if (!_reachable)
        {
            _reachable = true;
            LogRestored(_logger);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The Kodo journal store is not reachable. Records stay in the local journal and are replayed when it returns.")]
    private static partial void LogUnreachable(ILogger logger, Exception error);

    [LoggerMessage(Level = LogLevel.Information, Message = "The Kodo journal store is reachable again; the local backlog is being replayed.")]
    private static partial void LogRestored(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "The Kodo journal and the stored journal forked (stored head {StoredHead}, local next {LocalNext}). Nothing more is sent to the store.")]
    private static partial void LogForked(ILogger logger, long storedHead, long localNext);

    [LoggerMessage(Level = LogLevel.Error, Message = "The Kodo journal store refused the records ({Conflict}): {Reason} Nothing more is sent to the store.")]
    private static partial void LogConflict(ILogger logger, KodoJournalConflict conflict, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Journal records {From} to {To} are no longer held locally and are not in the store. Nothing more is sent to the store.")]
    private static partial void LogGap(ILogger logger, long from, long to);

    [LoggerMessage(Level = LogLevel.Information, Message = "Journal retention removed {Count} stored records.")]
    private static partial void LogPruned(ILogger logger, long count);
}
