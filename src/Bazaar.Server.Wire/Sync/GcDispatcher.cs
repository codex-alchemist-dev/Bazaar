using System.Collections.Concurrent;
using Bazaar.Contracts.Wire;

namespace Bazaar.Server.Wire.Sync;

/// <summary>
/// Fans new GC entries out to subscribed connections (doc 02 WP6). It consumes the <c>LISTEN</c> stream, but the
/// notification is only a prompt: each GC has one <see cref="GcFeed"/> that reads the log after its own high-water
/// mark, so a burst of notifications coalesces into one read and a lost or late notification costs latency, never data.
/// </summary>
public sealed class GcDispatcher : IDisposable
{
    private readonly ConcurrentDictionary<Guid, GcFeed> _feeds = new();
    private readonly IGcStreamSource _source;
    private readonly SyncOptions _options;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _stop = new();

    /// <summary>Creates a dispatcher over the log's read side.</summary>
    public GcDispatcher(IGcStreamSource source, SyncOptions options, TimeProvider? time = null)
    {
        _source = source;
        _options = options;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>GCs with at least one subscriber.</summary>
    public int ActiveFeeds => _feeds.Count;

    /// <summary>
    /// Consumes notifications until cancelled. <paramref name="sweepInterval"/> (when set) re-reads every active GC
    /// on that period, which bounds the delay a lost notification can cause even without a reconnect marker.
    /// </summary>
    public async Task RunAsync(IGcNotificationSource notifications, TimeSpan? sweepInterval, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        var sweep = sweepInterval is { } period ? SweepAsync(period, linked.Token) : Task.CompletedTask;
        try
        {
            await foreach (var note in notifications.ReadAllAsync(linked.Token).ConfigureAwait(false))
            {
                if (note.Reconnected)
                {
                    KickAll();
                }
                else if (_feeds.TryGetValue(note.GcId, out var feed))
                {
                    feed.Kick();
                }
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
        }
        finally
        {
            await linked.CancelAsync().ConfigureAwait(false);
            try
            {
                await sweep.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <summary>Marks a GC dirty as if a notification arrived. Also the hook for tests.</summary>
    public void Notify(Guid gcId)
    {
        if (_feeds.TryGetValue(gcId, out var feed))
        {
            feed.Kick();
        }
    }

    /// <summary>Re-checks every subscriber's access after a membership change; those no longer allowed get <c>stream.revoked</c>.</summary>
    public async Task RecheckAccessAsync(Guid gcId, IGcAccessPolicy access, CancellationToken ct)
    {
        if (!_feeds.TryGetValue(gcId, out var feed))
        {
            return;
        }

        foreach (var sub in feed.Snapshot())
        {
            if (!await access.CanReadAsync(sub.PrincipalId, gcId, ct).ConfigureAwait(false))
            {
                sub.Revoke();
            }
        }
    }

    /// <inheritdoc />
    public void Dispose() => _stop.Cancel();

    internal async ValueTask RegisterAsync(GcSubscription sub, CancellationToken ct)
    {
        GcFeed feed;
        while (true)
        {
            feed = _feeds.GetOrAdd(sub.GcId, id => new GcFeed(id, _source, _options, _time, _stop.Token));
            if (feed.TryAdd(sub))
            {
                break;
            }
        }

        sub.Closed += s => Unregister(feed, s);
        await feed.EnsureInitialisedAsync(ct).ConfigureAwait(false);
        feed.Kick();
    }

    internal void Unregister(GcSubscription sub)
    {
        if (_feeds.TryGetValue(sub.GcId, out var feed))
        {
            Unregister(feed, sub);
        }
    }

    private void Unregister(GcFeed feed, GcSubscription sub)
    {
        if (feed.Remove(sub))
        {
            _feeds.TryRemove(new KeyValuePair<Guid, GcFeed>(feed.GcId, feed));
        }
    }

    private void KickAll()
    {
        foreach (var feed in _feeds.Values)
        {
            feed.Kick();
        }
    }

    private async Task SweepAsync(TimeSpan period, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(period, _time);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            KickAll();
        }
    }

    /// <summary>The per-GC reader: one runner at a time, a dirty flag so notifications coalesce.</summary>
    private sealed class GcFeed(Guid gcId, IGcStreamSource source, SyncOptions options, TimeProvider time, CancellationToken stop)
    {
        private readonly object _gate = new();
        private Task? _init;
        private GcSubscription[] _subs = [];
        private bool _retired;
        private volatile bool _initialised;
        private long _hwN;
        private long _hwEv;
        private int _dirty;
        private int _running;

        public Guid GcId { get; } = gcId;

        public GcSubscription[] Snapshot() => Volatile.Read(ref _subs);

        public bool TryAdd(GcSubscription sub)
        {
            lock (_gate)
            {
                if (_retired)
                {
                    return false;
                }

                _subs = [.. _subs, sub];
                return true;
            }
        }

        /// <summary>Removes a subscriber; true when the feed is now empty and retired.</summary>
        public bool Remove(GcSubscription sub)
        {
            lock (_gate)
            {
                _subs = [.. _subs.Where(s => !ReferenceEquals(s, sub))];
                if (_subs.Length == 0)
                {
                    _retired = true;
                }

                return _retired;
            }
        }

        /// <summary>
        /// Sets the high-water mark to the log's head. Called after the first subscriber registered, so anything
        /// before the mark is visible to that subscriber's replay and anything after is delivered live.
        /// </summary>
        public async ValueTask EnsureInitialisedAsync(CancellationToken ct)
        {
            if (_initialised)
            {
                return;
            }

            Task init;
            lock (_gate)
            {
                init = _init ??= InitAsync();
            }

            try
            {
                await init.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!init.IsCompleted)
            {
                throw;
            }
            catch when (init.IsFaulted)
            {
                // Let the next subscriber try again.
                lock (_gate)
                {
                    if (ReferenceEquals(_init, init))
                    {
                        _init = null;
                    }
                }

                throw;
            }
        }

        private async Task InitAsync()
        {
            var head = await source.GetHeadAsync(GcId, stop).ConfigureAwait(false);
            _hwN = head?.LatestN ?? 0;
            _hwEv = head?.LatestEv ?? 0;
            _initialised = true;
        }

        public void Kick()
        {
            Volatile.Write(ref _dirty, 1);
            TryStart();
        }

        private void TryStart()
        {
            if (_initialised && Interlocked.CompareExchange(ref _running, 1, 0) == 0)
            {
                _ = Task.Run(PumpAsync, CancellationToken.None);
            }
        }

        private async Task PumpAsync()
        {
            while (true)
            {
                while (Interlocked.Exchange(ref _dirty, 0) == 1)
                {
                    try
                    {
                        await DrainAsync().ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested)
                    {
                        Volatile.Write(ref _running, 0);
                        return;
                    }
                    catch (Exception)
                    {
                        // The log could not be read. Keep the high-water mark, wait, and read again.
                        Volatile.Write(ref _dirty, 1);
                        try
                        {
                            await Task.Delay(options.PumpRetryDelay, time, stop).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            Volatile.Write(ref _running, 0);
                            return;
                        }
                    }
                }

                Volatile.Write(ref _running, 0);

                // A kick that landed between the last dirty check and the release must not be lost.
                if (Volatile.Read(ref _dirty) == 0 || Interlocked.CompareExchange(ref _running, 1, 0) != 0)
                {
                    return;
                }
            }
        }

        private async Task DrainAsync()
        {
            while (true)
            {
                if (Volatile.Read(ref _subs).Length == 0)
                {
                    return;
                }

                var page = await source.ReadAsync(GcId, _hwN, _hwEv, _hwEv, options.ReplayPageSize, stop).ConfigureAwait(false);
                if (page.Count == 0)
                {
                    return;
                }

                foreach (var message in page)
                {
                    _hwN = Math.Max(_hwN, message.N);
                    _hwEv = Math.Max(_hwEv, message.Ev);
                }

                // Snapshot after the read: a subscriber that registered before the read began is in it.
                foreach (var sub in Volatile.Read(ref _subs))
                {
                    sub.Deliver(page);
                }

                if (page.Count < options.ReplayPageSize)
                {
                    return;
                }
            }
        }
    }
}
