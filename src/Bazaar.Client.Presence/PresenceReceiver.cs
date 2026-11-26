using Bazaar.Client.Presence.Abstractions;
using Bazaar.Contracts.Wire;

namespace Bazaar.Client.Presence;

/// <summary>
/// Client settings for the presence receiver. <c>presence.reorder_window_ms</c> (docs/design/23, Settings) is a server-published
/// setting that the client does not read yet, so the window is an option with the doc default.
/// </summary>
public sealed class PresenceReceiverOptions
{
    /// <summary><c>presence.reorder_window_ms</c>: how long a delta that jumps ahead waits for the missing ones. 0 to 5000.</summary>
    public int ReorderWindowMs { get; init; } = 500;
}

/// <summary>
/// Applies agent presence deltas by the rule of docs/design/23 ("Sequence numbers on deltas"). A delta is applied only when its
/// revision is greater than the revision held for that agent; one that is not is stale and dropped. A delta that jumps ahead by
/// more than one is held for <see cref="PresenceReceiverOptions.ReorderWindowMs"/> for the missing ones, and when they do not
/// arrive the receiver replaces its rows with a fresh snapshot. An agent with no held row accepts any delta, since there is no
/// older revision to order against. The snapshot replaces rows outright, so a server that restarted and restarted its revisions
/// cannot leave the client ignoring it. Order never matters for correctness: the highest revision wins, and the server holds the truth.
/// Thread-safe; <see cref="Changed"/> is raised outside the lock.
/// </summary>
public sealed class PresenceReceiver : IAgentPresenceView, IDisposable
{
    private readonly Func<CancellationToken, Task<PresenceSnapshot>> _fetchSnapshot;
    private readonly TimeSpan _window;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, WirePresenceRow> _rows = [];
    private readonly Dictionary<Guid, Gap> _gaps = [];
    private readonly CancellationTokenSource _cts = new();
    private Task _refresh = Task.CompletedTask;
    private bool _refreshing;
    private bool _disposed;
    private int _snapshotRequests;

    /// <param name="fetchSnapshot">Asks the server for the snapshot of all rows.</param>
    /// <param name="options">The reorder window; null for the default.</param>
    /// <param name="time">Clock for the reorder window.</param>
    public PresenceReceiver(Func<CancellationToken, Task<PresenceSnapshot>> fetchSnapshot, PresenceReceiverOptions? options = null, TimeProvider? time = null)
    {
        _fetchSnapshot = fetchSnapshot ?? throw new ArgumentNullException(nameof(fetchSnapshot));
        var ms = (options ?? new PresenceReceiverOptions()).ReorderWindowMs;
        if (ms is < 0 or > 5000)
        {
            throw new ArgumentOutOfRangeException(nameof(options), ms, "presence.reorder_window_ms is 0 to 5000.");
        }

        _window = TimeSpan.FromMilliseconds(ms);
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public event Action<Guid>? Changed;

    /// <summary>How many snapshots the receiver asked for. For diagnostics and tests.</summary>
    public int SnapshotRequests => Volatile.Read(ref _snapshotRequests);

    /// <inheritdoc />
    public IReadOnlyList<WirePresenceRow> All
    {
        get
        {
            lock (_gate) return [.. _rows.Values.OrderBy(r => r.AgentId)];
        }
    }

    /// <inheritdoc />
    public WirePresenceRow? TryGetRow(Guid agentId)
    {
        lock (_gate) return _rows.GetValueOrDefault(agentId);
    }

    /// <summary>Completes when no snapshot fetch started by a gap is in flight.</summary>
    public Task WhenIdleAsync()
    {
        lock (_gate) return _refresh;
    }

    /// <summary>Applies one delta. Stale deltas are dropped; a jump ahead is held for the reorder window.</summary>
    public void Apply(PresenceUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (update.Kind != PresenceKinds.Agent || update.Row is null)
        {
            return;
        }

        var changed = new List<Guid>();
        var snapshotNow = false;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var id = update.AgentId;
            var held = _rows.TryGetValue(id, out var current) ? current.Revision : (long?)null;
            if (held is { } r && update.Revision <= r)
            {
                return;
            }

            if (held is null || update.Revision == held + 1)
            {
                _rows[id] = update.Row;
                changed.Add(id);
                Drain(id);
            }
            else
            {
                if (!_gaps.TryGetValue(id, out var gap))
                {
                    gap = new Gap();
                    _gaps[id] = gap;
                    if (_window > TimeSpan.Zero)
                    {
                        gap.Timer = _time.CreateTimer(OnWindowElapsed, id, _window, Timeout.InfiniteTimeSpan);
                    }
                    else
                    {
                        snapshotNow = true;
                    }
                }

                gap.Ahead[update.Revision] = update.Row;
            }
        }

        Raise(changed);
        if (snapshotNow)
        {
            StartRefresh();
        }
    }

    /// <summary>Replaces the held rows with a snapshot. Call it on connect and after a gap.</summary>
    public void ApplySnapshot(PresenceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var changed = new List<Guid>();
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var next = snapshot.Rows.ToDictionary(r => r.AgentId);
            foreach (var (id, row) in next)
            {
                if (!_rows.TryGetValue(id, out var old) || old != row)
                {
                    changed.Add(id);
                }
            }

            changed.AddRange(_rows.Keys.Where(id => !next.ContainsKey(id)));
            _rows.Clear();
            foreach (var (id, row) in next)
            {
                _rows[id] = row;
            }

            // Deltas held for a gap may be newer than the snapshot; keep the ones that continue it, drop the rest.
            foreach (var id in _gaps.Keys.ToArray())
            {
                if (_rows.ContainsKey(id) && Drain(id))
                {
                    changed.Add(id);
                }

                DropGap(id);
            }
        }

        Raise(changed);
    }

    /// <summary>Fetches the snapshot and applies it. Use on connect; failures leave the held rows as they are.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref _snapshotRequests);
        var snapshot = await _fetchSnapshot(ct).ConfigureAwait(false);
        ApplySnapshot(snapshot);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (var id in _gaps.Keys.ToArray())
            {
                DropGap(id);
            }
        }

        _cts.Cancel();
        _cts.Dispose();
    }

    /// <summary>Applies held deltas that now continue the held revision. Returns true when any applied. Call under the lock.</summary>
    private bool Drain(Guid id)
    {
        if (!_gaps.TryGetValue(id, out var gap))
        {
            return false;
        }

        var applied = false;
        while (true)
        {
            var next = _rows[id].Revision + 1;
            foreach (var stale in gap.Ahead.Keys.Where(k => k < next).ToArray())
            {
                gap.Ahead.Remove(stale);
            }

            if (!gap.Ahead.Remove(next, out var row))
            {
                break;
            }

            _rows[id] = row;
            applied = true;
        }

        if (gap.Ahead.Count == 0)
        {
            DropGap(id);
        }

        return applied;
    }

    private void DropGap(Guid id)
    {
        if (_gaps.Remove(id, out var gap))
        {
            gap.Timer?.Dispose();
        }
    }

    private void OnWindowElapsed(object? state)
    {
        var id = (Guid)state!;
        lock (_gate)
        {
            // Only a gap that is still open needs the snapshot; the missing deltas may have arrived in time.
            if (_disposed || !_gaps.ContainsKey(id))
            {
                return;
            }

            DropGap(id);
        }

        StartRefresh();
    }

    private void StartRefresh()
    {
        lock (_gate)
        {
            // One fetch at a time; the reply replaces every row, so a second request adds nothing.
            if (_disposed || _refreshing)
            {
                return;
            }

            _refreshing = true;
            _refresh = RunRefreshAsync();
        }
    }

    private async Task RunRefreshAsync()
    {
        try
        {
            await Task.Yield();
            await RefreshAsync(_cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The connection will fetch again on the next gap or reconnect; the held rows stay as they were.
        }
        catch (OperationCanceledException)
        {
            // Disposed.
        }
        finally
        {
            lock (_gate) _refreshing = false;
        }
    }

    private void Raise(List<Guid> ids)
    {
        foreach (var id in ids.Distinct())
        {
            Changed?.Invoke(id);
        }
    }

    private sealed class Gap
    {
        public Dictionary<long, WirePresenceRow> Ahead { get; } = [];

        public ITimer? Timer { get; set; }
    }
}
