using System.Diagnostics.CodeAnalysis;

namespace Bazaar.Server.Memory.Write;

/// <summary>
/// Per-agent queue of memory writes. Each accepted write is spooled first, then held until one
/// commit flushes it: at end of turn (<see cref="FlushTurnAsync"/>), once it has been idle for
/// <c>idleSeconds</c> (the host calls <see cref="RunIdleCheckAsync"/> periodically), or when
/// <c>maxWrites</c> are queued. Flushes are serialized. A failed commit keeps the writes queued and spooled.
/// </summary>
[SuppressMessage("Naming", "CA1711", Justification = "MemoryWriteQueue is the name fixed by docs/design/08-memory-system.md section 3.")]
public sealed class MemoryWriteQueue : IDisposable
{
    private readonly string _agentId;
    private readonly IMemoryBatchSink _sink;
    private readonly IMemorySpool _spool;
    private readonly TimeProvider _time;
    private readonly TimeSpan _idle;
    private readonly int _maxWrites;

    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private readonly object _sync = new();
    private readonly List<MemoryWriteItem> _pending = [];
    private readonly HashSet<Guid> _pendingIds = [];
    private readonly HashSet<Guid> _committedIds = [];
    private DateTimeOffset _lastEnqueueUtc;

    /// <summary>Creates the queue for one agent.</summary>
    /// <param name="agentId">Agent whose writes this queue batches.</param>
    /// <param name="sink">Commits each flushed batch.</param>
    /// <param name="spool">Durable record of accepted writes.</param>
    /// <param name="time">Clock for the idle window.</param>
    /// <param name="idleSeconds">Seconds without an enqueue before an idle flush. Default 30 (<c>memory.batch.idle_seconds</c>).</param>
    /// <param name="maxWrites">Queued writes that force a flush. Default 20 (<c>memory.batch.max_writes</c>).</param>
    public MemoryWriteQueue(
        string agentId,
        IMemoryBatchSink sink,
        IMemorySpool spool,
        TimeProvider time,
        int idleSeconds = 30,
        int maxWrites = 20)
    {
        ArgumentException.ThrowIfNullOrEmpty(agentId);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(spool);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentOutOfRangeException.ThrowIfLessThan(idleSeconds, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxWrites, 1);

        _agentId = agentId;
        _sink = sink;
        _spool = spool;
        _time = time;
        _idle = TimeSpan.FromSeconds(idleSeconds);
        _maxWrites = maxWrites;
        _lastEnqueueUtc = time.GetUtcNow();
    }

    /// <summary>Releases the flush gate. Call when the agent's queue is retired; do not dispose during a flush.</summary>
    public void Dispose() => _flushGate.Dispose();

    /// <summary>Writes queued and not yet committed.</summary>
    public int PendingCount
    {
        get
        {
            lock (_sync)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>
    /// Spools and accepts one write. A write whose id is already pending or committed is ignored.
    /// Reaching <c>maxWrites</c> flushes at once.
    /// </summary>
    public async Task EnqueueAsync(MemoryWriteItem item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        lock (_sync)
        {
            if (_pendingIds.Contains(item.WriteId) || _committedIds.Contains(item.WriteId))
            {
                return;
            }
        }

        await _spool.AppendAsync(item, ct).ConfigureAwait(false);

        bool full;
        lock (_sync)
        {
            if (!_pendingIds.Add(item.WriteId))
            {
                return;
            }

            _pending.Add(item);
            _lastEnqueueUtc = _time.GetUtcNow();
            full = _pending.Count >= _maxWrites;
        }

        if (full)
        {
            await FlushAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Flushes everything pending as one commit. Call at the end of each agent turn.</summary>
    public Task FlushTurnAsync(CancellationToken ct = default) => FlushAsync(ct);

    /// <summary>Flushes when writes are pending and the idle window since the last enqueue has passed.</summary>
    /// <returns>True when a flush was attempted.</returns>
    public async Task<bool> RunIdleCheckAsync(CancellationToken ct = default)
    {
        bool due;
        lock (_sync)
        {
            due = _pending.Count > 0 && _time.GetUtcNow() - _lastEnqueueUtc >= _idle;
        }

        if (due)
        {
            await FlushAsync(ct).ConfigureAwait(false);
        }

        return due;
    }

    /// <summary>
    /// Returns the pending content for a path, so a read in the same turn sees a store made before the flush.
    /// The last pending write to the path wins.
    /// </summary>
    public bool TryGetPending(string path, [NotNullWhen(true)] out string? content)
    {
        ArgumentNullException.ThrowIfNull(path);

        lock (_sync)
        {
            for (var i = _pending.Count - 1; i >= 0; i--)
            {
                if (string.Equals(_pending[i].Path, path, StringComparison.Ordinal))
                {
                    content = _pending[i].Content;
                    return true;
                }
            }
        }

        content = null;
        return false;
    }

    /// <summary>
    /// Loads the spool after a restart. Writes already committed (<paramref name="committedWriteIds"/>, read
    /// from the repo by the caller) are dropped from the spool. Other writes are queued without a second spool
    /// append. Writes already pending are skipped, so calling this twice does not duplicate anything.
    /// </summary>
    public async Task ReplayAsync(IReadOnlySet<Guid> committedWriteIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(committedWriteIds);

        var stored = await _spool.ReadAllAsync(ct).ConfigureAwait(false);
        var alreadyCommitted = new List<Guid>();

        lock (_sync)
        {
            var requeued = false;
            foreach (var item in stored)
            {
                if (_pendingIds.Contains(item.WriteId))
                {
                    continue;
                }

                if (_committedIds.Contains(item.WriteId) || committedWriteIds.Contains(item.WriteId))
                {
                    _committedIds.Add(item.WriteId);
                    alreadyCommitted.Add(item.WriteId);
                    continue;
                }

                _pendingIds.Add(item.WriteId);
                _pending.Add(item);
                requeued = true;
            }

            if (requeued)
            {
                _lastEnqueueUtc = _time.GetUtcNow();
            }
        }

        if (alreadyCommitted.Count > 0)
        {
            await _spool.RemoveAsync(alreadyCommitted, ct).ConfigureAwait(false);
        }
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        await _flushGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            List<MemoryWriteItem> snapshot;
            lock (_sync)
            {
                snapshot = [.. _pending];
            }

            if (snapshot.Count == 0)
            {
                return;
            }

            var batch = MemoryBatch.Create(_agentId, snapshot);
            var result = await _sink.CommitAsync(batch, ct).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                return;
            }

            var committed = snapshot.Select(i => i.WriteId).ToList();
            var committedSet = committed.ToHashSet();
            lock (_sync)
            {
                _pending.RemoveAll(i => committedSet.Contains(i.WriteId));
                foreach (var id in committed)
                {
                    _pendingIds.Remove(id);
                    _committedIds.Add(id);
                }
            }

            await _spool.RemoveAsync(committed, ct).ConfigureAwait(false);
        }
        finally
        {
            _flushGate.Release();
        }
    }
}
