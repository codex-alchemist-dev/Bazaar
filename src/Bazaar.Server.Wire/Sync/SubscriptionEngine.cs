using Bazaar.Contracts.Gc;
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
    TimeProvider? time = null,
    IGraphLodSource? graphLod = null,
    IEnumerable<IStreamGate>? gates = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly IStreamGate[] _gates = gates is null ? [] : [.. gates];

    /// <summary>Creates the per-connection state. Dispose it when the socket closes.</summary>
    public SubscriptionSession CreateSession(Guid principalId, PrincipalKind principalKind, IWireCodec codec, IConnectionOutbound outbound) =>
        new(principalId, principalKind, source, access, dispatcher, options, new SyncFrames(codec, options, _time), outbound, graphLod, _gates);
}

/// <summary>One connection's subscriptions. Thread-safe: <c>subscribe</c> requests may overlap.</summary>
public sealed class SubscriptionSession : ISubscriptionSession
{
    private readonly Guid _principalId;
    private readonly PrincipalKind _principalKind;
    private readonly IGcStreamSource _source;
    private readonly IGcAccessPolicy _access;
    private readonly GcDispatcher _dispatcher;
    private readonly SyncOptions _options;
    private readonly SyncFrames _frames;
    private readonly IConnectionOutbound _outbound;
    private readonly IGraphLodSource? _graphLod;
    private readonly IStreamGate[] _gates;
    private readonly HashSet<string> _gated = [];
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, GcSubscription> _subs = [];

    internal SubscriptionSession(
        Guid principalId, PrincipalKind principalKind, IGcStreamSource source, IGcAccessPolicy access, GcDispatcher dispatcher,
        SyncOptions options, SyncFrames frames, IConnectionOutbound outbound, IGraphLodSource? graphLod, IStreamGate[] gates)
    {
        _principalId = principalId;
        _principalKind = principalKind;
        _source = source;
        _access = access;
        _dispatcher = dispatcher;
        _options = options;
        _frames = frames;
        _outbound = outbound;
        _graphLod = graphLod;
        _gates = gates;
    }

    /// <summary>Streams this connection is subscribed to.</summary>
    public IReadOnlyCollection<string> Streams
    {
        get { lock (_gate) { return [.. _subs.Keys, .. _gated]; } }
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
                _gated.Remove(name);
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
            _gated.Clear();
        }

        _cts.Cancel();
        foreach (var sub in all)
        {
            sub.Dispose();
        }

        _cts.Dispose();
    }

    /// <summary>
    /// A graph stream is a one-shot level-of-detail snapshot (doc 19). The source checks access and returns the frame, or
    /// null for denied and unknown graphs alike. Graph streams are not held in <c>_subs</c>, so <c>unsubscribe</c> has
    /// nothing to drop and resubscribing sends a fresh snapshot. Live deltas are a later change.
    /// </summary>
    private async Task<(string Status, Func<Task>? Start)> SubscribeGraphAsync(string stream, Guid graphId, CancellationToken ct)
    {
        if (_graphLod is null)
        {
            return (SubscribeStatuses.Denied, null);
        }

        var frame = await _graphLod.GetAsync(_principalId, graphId, ct).ConfigureAwait(false);
        if (frame is null)
        {
            return (SubscribeStatuses.Denied, null);
        }

        var outgoing = frame with { S = stream };
        return (SubscribeStatuses.Ok, () => _outbound.EnqueueAsync(_frames.GraphLod(outgoing), _cts.Token).AsTask());
    }

    /// <summary>
    /// A non-GC, non-graph stream (<c>presence</c>) is subscribable only when a registered <see cref="IStreamGate"/> names it and
    /// allows the principal. There is no replay: the client fetches current state with a request. Without a gate the answer is
    /// <c>denied</c>, which is also the answer for unknown names.
    /// </summary>
    private async Task<string> SubscribeGatedAsync(string stream, CancellationToken ct)
    {
        var gate = Array.Find(_gates, g => string.Equals(g.Stream, stream, StringComparison.Ordinal));
        if (gate is null || !await gate.CanSubscribeAsync(_principalId, _principalKind, ct).ConfigureAwait(false))
        {
            return SubscribeStatuses.Denied;
        }

        lock (_gate)
        {
            _gated.Add(stream);
        }

        return SubscribeStatuses.Ok;
    }

    private async Task<(string Status, Func<Task>? Start)> SubscribeOneAsync(StreamCursor cursor, CancellationToken ct)
    {
        if (WireStreams.TryParseGraph(cursor.S, out var graphId))
        {
            return await SubscribeGraphAsync(cursor.S, graphId, ct).ConfigureAwait(false);
        }

        if (!WireStreams.TryParseGc(cursor.S, out var gcId))
        {
            return (await SubscribeGatedAsync(cursor.S, ct).ConfigureAwait(false), null);
        }

        if (!await _access.CanReadAsync(_principalId, _principalKind, gcId, ct).ConfigureAwait(false))
        {
            return (SubscribeStatuses.Denied, null);
        }

        var after = Math.Max(cursor.After, 0);
        var editAfter = Math.Max(cursor.EditAfter, 0);
        var sub = new GcSubscription(gcId, cursor.S, _principalId, _principalKind, after, editAfter, _outbound, _frames, _options, _cts.Token);
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
