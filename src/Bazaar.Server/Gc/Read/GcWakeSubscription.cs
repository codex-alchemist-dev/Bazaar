using System.Threading.Channels;

namespace Bazaar.Server.Gc.Read;

/// <summary>What woke a subscriber.</summary>
/// <param name="Resync">True after the listener reconnected: hints may have been missed, so compare every GC's head with its cursor.</param>
/// <param name="Heads">Newest seq hinted per GC since the subscriber last asked.</param>
public sealed record GcWake(bool Resync, IReadOnlyDictionary<Guid, long> Heads);

/// <summary>
/// One consumer's view of the listener. Hints are coalesced to the newest seq per GC, so a slow consumer holds at most one entry
/// per GC and never blocks the listener. A hint is only a wake-up: read the log from your cursor.
/// </summary>
public sealed class GcWakeSubscription : IDisposable
{
    private readonly GcNotifyListener _owner;
    private readonly Guid? _filter;
    private readonly Lock _gate = new();
    private readonly Channel<bool> _signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private Dictionary<Guid, long> _heads = [];
    private bool _resync;

    internal GcWakeSubscription(GcNotifyListener owner, Guid? filter)
    {
        _owner = owner;
        _filter = filter;
    }

    internal void Publish(Guid gcId, long seq)
    {
        if (_filter is { } f && f != gcId)
            return;
        lock (_gate)
            _heads[gcId] = _heads.TryGetValue(gcId, out var old) && old > seq ? old : seq;
        _signal.Writer.TryWrite(true);
    }

    internal void PublishResync()
    {
        lock (_gate)
            _resync = true;
        _signal.Writer.TryWrite(true);
    }

    /// <summary>Takes whatever is pending without waiting, or null when nothing is.</summary>
    public GcWake? TryTake()
    {
        lock (_gate)
        {
            if (!_resync && _heads.Count == 0)
                return null;
            var wake = new GcWake(_resync, _heads);
            _heads = [];
            _resync = false;
            return wake;
        }
    }

    /// <summary>Waits up to <paramref name="timeout"/> for a wake; null on timeout.</summary>
    public async Task<GcWake?> WaitAsync(TimeSpan timeout, TimeProvider time, CancellationToken ct)
    {
        var timer = new CancellationTokenSource(timeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timer.Token);
        try
        {
            while (true)
            {
                if (TryTake() is { } wake)
                    return wake;
                await _signal.Reader.WaitToReadAsync(linked.Token).ConfigureAwait(false);
                _signal.Reader.TryRead(out _);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return TryTake();
        }
        finally
        {
            timer.Dispose();
        }
    }

    public void Dispose() => _owner.Unsubscribe(this);
}
