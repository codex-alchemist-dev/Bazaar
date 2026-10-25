using Bazaar.Contracts.Wire;

namespace Bazaar.Server.Wire.Sync;

/// <summary>
/// One connection's subscription to one GC stream. It starts <c>Buffering</c> live events while the replay
/// pages out of the log, then splices to <c>Live</c> under <see cref="_gate"/>. Events are de-duplicated by
/// <c>ev</c>, which rises in commit order, so anything the replay already sent is dropped.
/// </summary>
internal sealed class GcSubscription : IDisposable
{
    private enum State { Buffering, Live, Closed }

    private readonly object _gate = new();
    private readonly IConnectionOutbound _outbound;
    private readonly SyncFrames _frames;
    private readonly SyncOptions _options;
    private readonly CancellationTokenSource _cts;
    private readonly List<WireGcMessage> _buffer = [];
    private State _state = State.Buffering;
    private bool _overflow;
    private long _lastN;
    private long _lastEv;

    public GcSubscription(
        Guid gcId, string stream, Guid principalId, long after, long editAfter,
        IConnectionOutbound outbound, SyncFrames frames, SyncOptions options, CancellationToken sessionCt)
    {
        GcId = gcId;
        Stream = stream;
        PrincipalId = principalId;
        After = after;
        EditAfter = editAfter;
        _lastN = after;
        _lastEv = editAfter;
        _outbound = outbound;
        _frames = frames;
        _options = options;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(sessionCt);
        Token = _cts.Token;
    }

    public Guid GcId { get; }

    public string Stream { get; }

    public Guid PrincipalId { get; }

    public long After { get; }

    public long EditAfter { get; }

    public CancellationToken Token { get; }

    public bool IsClosed
    {
        get { lock (_gate) { return _state == State.Closed; } }
    }

    /// <summary>Raised once when the subscription closes, so the session and the feed can drop it.</summary>
    public event Action<GcSubscription>? Closed;

    /// <summary>Called by the feed's pump with new entries in ascending <c>ev</c> order.</summary>
    public void Deliver(IReadOnlyList<WireGcMessage> items)
    {
        var lagged = false;
        lock (_gate)
        {
            switch (_state)
            {
                case State.Buffering:
                    if (!_overflow)
                    {
                        _buffer.AddRange(items);
                        if (_buffer.Count > _options.LiveBufferMax)
                        {
                            // Too much arrived during the replay: drop it and let the replay read it from the log.
                            _buffer.Clear();
                            _overflow = true;
                        }
                    }

                    break;
                case State.Live:
                    lagged = !SendLiveLocked(items);
                    break;
            }
        }

        if (lagged)
        {
            Close();
        }
    }

    /// <summary>Replays from the log until contiguous with the live feed, then goes live.</summary>
    public async Task ReplayAsync(IGcStreamSource source, CancellationToken ct)
    {
        var pageEv = 0L;
        var pageSize = _options.ReplayPageSize;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (IsClosed)
            {
                return;
            }

            var page = await source.ReadAsync(GcId, After, EditAfter, pageEv, pageSize, ct).ConfigureAwait(false);
            foreach (var message in page)
            {
                await _outbound.EnqueueAsync(_frames.Message(Stream, message), ct).ConfigureAwait(false);
                _lastN = Math.Max(_lastN, message.N);
                _lastEv = Math.Max(_lastEv, message.Ev);
                pageEv = Math.Max(pageEv, message.Ev);
            }

            if (page.Count >= pageSize)
            {
                continue;
            }

            if (TryGoLive())
            {
                return;
            }
        }
    }

    /// <summary>Skips replay (a <c>resync</c> was sent) and goes live from the given position.</summary>
    public void GoLiveFrom(long n, long ev)
    {
        bool lagged;
        lock (_gate)
        {
            if (_state != State.Buffering)
            {
                return;
            }

            _lastN = Math.Max(_lastN, n);
            _lastEv = Math.Max(_lastEv, ev);
            lagged = !FlushAndGoLiveLocked();
        }

        if (lagged)
        {
            Close();
        }
    }

    /// <summary>Tells the client live delivery stopped (<c>lagged</c>) and closes. Used when the log cannot be read.</summary>
    public void Fail()
    {
        if (TryCloseWith(_frames.Lagged(Stream, _lastN)))
        {
            Closed?.Invoke(this);
        }
    }

    /// <summary>Access ended: tells the client and closes.</summary>
    public void Revoke()
    {
        if (TryCloseWith(_frames.Revoked(Stream)))
        {
            Closed?.Invoke(this);
        }
    }

    /// <summary>Closes silently (unsubscribe, replaced by a newer subscription, connection gone).</summary>
    public void Dispose()
    {
        bool wasOpen;
        lock (_gate)
        {
            wasOpen = _state != State.Closed;
            _state = State.Closed;
            _buffer.Clear();
        }

        _cts.Cancel();
        if (wasOpen)
        {
            Closed?.Invoke(this);
        }

        _cts.Dispose();
    }

    private void Close() => Fail();

    private bool TryCloseWith(Envelope control)
    {
        lock (_gate)
        {
            if (_state == State.Closed)
            {
                return false;
            }

            _state = State.Closed;
            _buffer.Clear();
            _outbound.EnqueueControl(control);
        }

        _cts.Cancel();
        return true;
    }

    private bool TryGoLive()
    {
        lock (_gate)
        {
            if (_state == State.Closed)
            {
                return true;
            }

            if (_overflow)
            {
                _overflow = false;
                _buffer.Clear();
                return false;
            }

            if (FlushAndGoLiveLocked())
            {
                return true;
            }
        }

        Close();
        return true;
    }

    // Returns false when the outbound queue was full, so the caller closes with `lagged`.
    private bool FlushAndGoLiveLocked()
    {
        _state = State.Live;
        if (_overflow)
        {
            // Buffered entries were dropped and nothing re-read them; the client must resubscribe.
            _buffer.Clear();
            return false;
        }

        var ok = SendLiveLocked(_buffer);
        _buffer.Clear();
        return ok;
    }

    private bool SendLiveLocked(IReadOnlyList<WireGcMessage> items)
    {
        foreach (var message in items)
        {
            if (message.Ev <= _lastEv)
            {
                continue;
            }

            if (!_outbound.TryEnqueue(_frames.Message(Stream, message)))
            {
                return false;
            }

            _lastN = Math.Max(_lastN, message.N);
            _lastEv = message.Ev;
        }

        return true;
    }
}
