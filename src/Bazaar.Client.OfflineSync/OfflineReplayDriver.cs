using Bazaar.OfflineSync;

namespace Bazaar.Client.OfflineSync;

/// <summary>How a replay run ended.</summary>
public enum OfflineReplayStatus
{
    /// <summary>The queue is empty: every pending write is acknowledged.</summary>
    Drained = 1,

    /// <summary>The transport failed. The unacknowledged writes stay queued for the next run.</summary>
    Offline = 2,

    /// <summary>Progress stopped (a server contract break, an unclearable gap, or a corrupted queue). A person needs to look.</summary>
    Stalled = 3,
}

/// <summary>The result of one replay run.</summary>
/// <param name="Status">How the run ended.</param>
/// <param name="AckedSequence">Highest device sequence acknowledged in this run.</param>
/// <param name="AttentionCount">Writes that need a person in this run (Conflict, HeldForReview, Rejected), plus one for a stall.</param>
/// <param name="Reason">Why the run stopped early; null when drained.</param>
/// <param name="RepairedLostWrites">Writes lost to a torn-tail repair when the queue opened (doc 56 section 6). Already counted in <paramref name="AttentionCount"/>.</param>
public sealed record OfflineReplayResult(
    OfflineReplayStatus Status,
    long AckedSequence,
    int AttentionCount,
    string? Reason = null,
    long RepairedLostWrites = 0);

/// <summary>
/// Replays the queue to the server (doc 56 section 6). Sends the front batch, acknowledges the queue up to the server's AckedSequence,
/// resends from a reported gap, and goes offline on transport failure. Conflict, HeldForReview and Rejected outcomes are counted
/// into Attention.
/// </summary>
public sealed class OfflineReplayDriver
{
    /// <summary>The default number of writes sent per batch.</summary>
    public const int DefaultBatchSize = 100;

    private const int _maxGapRetries = 3;

    private readonly EncryptedOfflineQueue _queue;
    private readonly IOfflineSyncTransport _transport;
    private readonly ConnectionStateTracker _state;
    private readonly int _batchSize;
    private readonly TimeProvider _time;

    /// <summary>Creates a driver.</summary>
    /// <param name="queue">The queue to drain.</param>
    /// <param name="transport">Sends batches to the server.</param>
    /// <param name="state">Receives connection state changes.</param>
    /// <param name="batchSize">Most writes per batch.</param>
    /// <param name="time">Clock for the Offline "as of" time; null for the system clock.</param>
    public OfflineReplayDriver(
        EncryptedOfflineQueue queue,
        IOfflineSyncTransport transport,
        ConnectionStateTracker state,
        int batchSize = DefaultBatchSize,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        _queue = queue;
        _transport = transport;
        _state = state;
        _batchSize = batchSize;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Runs one pass until the queue is drained, the transport fails, or progress stops.</summary>
    public async Task<OfflineReplayResult> RunAsync(CancellationToken cancellationToken)
    {
        var lost = _queue.Repair?.LostWrites ?? 0;
        var run = new RunProgress(_queue.PendingCount, lost);
        try
        {
            return await ReplayAsync(run, cancellationToken).ConfigureAwait(false);
        }
        catch (OfflineQueueCorruptedException ex)
        {
            return Stall(run, $"the queue is corrupted: {ex.Message}");
        }
    }

    private async Task<OfflineReplayResult> ReplayAsync(RunProgress run, CancellationToken cancellationToken)
    {
        long? expectedFront = null;
        var noProgress = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batch = _queue.PeekBatch(_batchSize);
            if (batch.Count == 0)
            {
                _state.Set(run.Attention > 0 ? new ConnectionState.Attention(run.Attention) : new ConnectionState.Live());
                return new OfflineReplayResult(OfflineReplayStatus.Drained, run.Acked, run.Attention, null, run.RepairedLostWrites);
            }

            var front = batch[0].DeviceSequence;
            var last = batch[^1].DeviceSequence;
            if (expectedFront is { } expected && front != expected)
            {
                return Stall(run, $"after a gap at {expected} the queue starts at {front}");
            }

            var pending = _queue.PendingCount;
            run.Total = Math.Max(run.Total, pending);
            _state.Set(new ConnectionState.Syncing(run.Total - pending, run.Total));

            OfflineBatchResult result;
            try
            {
                result = await _transport.SendAsync(new OfflineWriteBatch(_queue.DeviceId, batch), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _state.Set(new ConnectionState.Offline(_time.GetUtcNow()));
                return new OfflineReplayResult(OfflineReplayStatus.Offline, run.Acked, run.Attention, ex.Message, run.RepairedLostWrites);
            }

            run.Attention += result.Outcomes.Count(o =>
                o.Kind is OfflineOutcomeKind.Conflict or OfflineOutcomeKind.HeldForReview or OfflineOutcomeKind.Rejected);

            var ackTo = Math.Min(result.AckedSequence, last);
            var before = run.Acked;
            if (ackTo > run.Acked)
            {
                _queue.Acknowledge(ackTo);
                run.Acked = ackTo;
            }

            var progressed = run.Acked > before;
            if (result.GapAtSequence is { } gap)
            {
                if (result.AckedSequence + 1 != gap)
                {
                    return Stall(run, $"the server reported a gap at {gap} after acknowledging {result.AckedSequence}");
                }

                noProgress = progressed ? 0 : noProgress + 1;
                if (noProgress > _maxGapRetries)
                {
                    return Stall(run, $"the gap at {gap} did not clear after {_maxGapRetries} resends");
                }

                expectedFront = gap;
                continue;
            }

            if (ackTo < last)
            {
                return Stall(run, $"the server acknowledged {result.AckedSequence} for a batch ending at {last} without reporting a gap");
            }

            expectedFront = null;
            noProgress = 0;
        }
    }

    private OfflineReplayResult Stall(RunProgress run, string reason)
    {
        run.Attention++;
        _state.Set(new ConnectionState.Attention(run.Attention));
        return new OfflineReplayResult(OfflineReplayStatus.Stalled, run.Acked, run.Attention, reason, run.RepairedLostWrites);
    }

    private sealed class RunProgress(long total, long repairedLostWrites)
    {
        public long Total { get; set; } = total;

        public long Acked { get; set; }

        /// <summary>Writes lost to the torn-tail repair at open. Each one needs a person to redo it, so each counts as Attention.</summary>
        public long RepairedLostWrites { get; } = repairedLostWrites;

        public int Attention { get; set; } = checked((int)repairedLostWrites);
    }
}
