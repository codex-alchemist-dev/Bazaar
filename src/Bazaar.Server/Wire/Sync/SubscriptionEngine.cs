using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;

namespace Bazaar.Server.Wire.Sync;

/// <summary>
/// Process-wide subscription logic (doc 02 WP5): access check, <c>(n, ev)</c> replay paging, splice to live,
/// <c>resync</c> past the cutoff. The connection manager (W3) creates one <see cref="SubscriptionSession"/> per
/// authenticated connection and routes <c>subscribe</c>, <c>unsubscribe</c> and socket close to it.
/// </summary>
public sealed class SubscriptionEngine(
    IGcStreamSource source,
    IGcAccessPolicy access,
    GcDispatcher dispatcher,
    SyncOptions options,
    TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Creates the per-connection state. Dispose it when the socket closes.</summary>
    public SubscriptionSession CreateSession(Guid principalId, IWireCodec codec, IConnectionOutbound outbound) =>
        new(principalId, source, access, dispatcher, options, new SyncFrames(codec, options, _time), outbound);
}

/// <summary>What <see cref="SubscriptionSession.SubscribeAsync"/> decided, and the work to start once the response is queued.</summary>
public sealed class SubscribeOutcome
{
    private readonly IReadOnlyList<Func<Task>> _starts;

    internal SubscribeOutcome(SubscribeResult result, IReadOnlyList<Func<Task>> starts)
    {
        Result = result;
        _starts = starts;
    }

    /// <summary>The <c>res</c> payload; send it before calling <see cref="StartAsync"/> so replay never overtakes it.</summary>
    public SubscribeResult Result { get; }

    /// <summary>Runs replay (or sends <c>resync</c>) for every accepted stream. Completes when each stream is live or closed.</summary>
    public Task StartAsync() => Task.WhenAll(_starts.Select(start => start()));
}

/// <summary>One connection's subscriptions. Thread-safe: <c>subscribe</c> requests may overlap.</summary>
public sealed class SubscriptionSession : IDisposable
{
    private readonly Guid _principalId;
    private readonly IGcStreamSource _source;
    private readonly IGcAccessPolicy _access;
    private readonly GcDispatcher _dispatcher;
    private readonly SyncOptions _options;
    private readonly SyncFrames _frames;
    private readonly IConnectionOutbound _outbound;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, GcSubscription> _subs = [];

    internal SubscriptionSession(
        Guid principalId, IGcStreamSource source, IGcAccessPolicy access, GcDispatcher dispatcher,
        SyncOptions options, SyncFrames frames, IConnectionOutbound outbound)
    {
        _principalId = principalId;
        _source = source;
        _access = access;
        _dispatcher = dispatcher;
        _options = options;
        _frames = frames;
        _outbound = outbound;
    }

    /// <summary>Streams this connection is subscribed to.</summary>
    public IReadOnlyCollection<string> Streams
    {
        get { lock (_gate) { return [.. _subs.Keys]; } }
    }

    /// <summary>
    /// Checks access and registers for live events on each stream, then returns. A stream gets <c>ok</c>, <c>denied</c>
    /// (also for unknown streams and GCs, so membership does not leak) or <c>resync</c>. Resubscribing to a stream
    /// replaces the old subscription, which is how a client recovers from <c>lagged</c>.
    /// </summary>
    public async Task<SubscribeOutcome> SubscribeAsync(Subscribe request, CancellationToken ct)
    {
        var results = new List<StreamResult>(request.Streams.Count);
        var starts = new List<Func<Task>>();
        foreach (var cursor in request.Streams.DistinctBy(c => c.S))
        {
            var (status, start) = await SubscribeOneAsync(cursor, ct).ConfigureAwait(false);
            results.Add(new StreamResult(cursor.S, status));
            if (start is not null)
            {
                starts.Add(start);
            }
        }

        return new SubscribeOutcome(new SubscribeResult(results), starts);
    }

    /// <summary>Drops streams; unknown names are ignored.</summary>
    public void Unsubscribe(IEnumerable<string> streams)
    {
        foreach (var name in streams)
        {
            GcSubscription? sub;
            lock (_gate)
            {
                _subs.Remove(name, out sub);
            }

            sub?.Dispose();
        }
    }

    /// <summary>Closes every subscription and cancels replays in flight.</summary>
    public void Dispose()
    {
        List<GcSubscription> all;
        lock (_gate)
        {
            all = [.. _subs.Values];
            _subs.Clear();
        }

        _cts.Cancel();
        foreach (var sub in all)
        {
            sub.Dispose();
        }

        _cts.Dispose();
    }

    private async Task<(string Status, Func<Task>? Start)> SubscribeOneAsync(StreamCursor cursor, CancellationToken ct)
    {
        if (!WireStreams.TryParseGc(cursor.S, out var gcId))
        {
            return (SubscribeStatuses.Denied, null);
        }

        if (!await _access.CanReadAsync(_principalId, gcId, ct).ConfigureAwait(false))
        {
            return (SubscribeStatuses.Denied, null);
        }

        var after = Math.Max(cursor.After, 0);
        var editAfter = Math.Max(cursor.EditAfter, 0);
        var sub = new GcSubscription(gcId, cursor.S, _principalId, after, editAfter, _outbound, _frames, _options, _cts.Token);
        Replace(cursor.S, sub);

        // Register before reading the head: anything committed after this point is buffered, anything before is
        // in the log the replay reads.
        GcHead? head;
        try
        {
            await _dispatcher.RegisterAsync(sub, ct).ConfigureAwait(false);
            head = await _source.GetHeadAsync(gcId, ct).ConfigureAwait(false);
        }
        catch
        {
            Forget(cursor.S, sub);
            throw;
        }

        if (head is not { } h)
        {
            Forget(cursor.S, sub);
            return (SubscribeStatuses.Denied, null);
        }

        // A cursor ahead of the head means a restored database (stale_cursor); the client resyncs that stream.
        var stale = after > h.LatestN || editAfter > h.LatestEv;
        var gap = (h.LatestN - after) + (h.LatestEv - editAfter);
        if (stale || gap > _options.ReplayCutoffEntries)
        {
            return (SubscribeStatuses.Resync, () => RunAsync(sub, h, resync: true));
        }

        return (SubscribeStatuses.Ok, () => RunAsync(sub, h, resync: false));
    }

    private async Task RunAsync(GcSubscription sub, GcHead head, bool resync)
    {
        var ct = sub.Token;
        try
        {
            if (resync)
            {
                var tail = await _source.ReadTailAsync(sub.GcId, _options.ResyncTailSize, ct).ConfigureAwait(false);
                var unread = await _source.CountUnreadAsync(sub.GcId, _principalId, ct).ConfigureAwait(false);
                await _outbound.EnqueueAsync(_frames.Resync(sub.Stream, head, unread, tail), ct).ConfigureAwait(false);
                var tailN = tail.Count == 0 ? 0 : tail.Max(m => m.N);
                var tailEv = tail.Count == 0 ? 0 : tail.Max(m => m.Ev);
                sub.GoLiveFrom(Math.Max(head.LatestN, tailN), Math.Max(head.LatestEv, tailEv));
            }
            else
            {
                await sub.ReplayAsync(_source, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Unsubscribed, replaced, revoked or the connection closed.
        }
        catch (Exception)
        {
            // The log could not be read: tell the client to resubscribe from its cursor.
            sub.Fail();
        }
    }

    private void Replace(string stream, GcSubscription sub)
    {
        GcSubscription? old;
        lock (_gate)
        {
            _subs.TryGetValue(stream, out old);
            _subs[stream] = sub;
        }

        sub.Closed += s => Forget(stream, s);
        old?.Dispose();
    }

    private void Forget(string stream, GcSubscription sub)
    {
        lock (_gate)
        {
            if (_subs.TryGetValue(stream, out var current) && ReferenceEquals(current, sub))
            {
                _subs.Remove(stream);
            }
        }

        sub.Dispose();
    }
}
