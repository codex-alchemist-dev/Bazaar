namespace Bazaar.Server.VmDisplay;

/// <summary>
/// A per-key sliding-window limiter. Memory is bounded: at most <c>maxTrackedKeys</c> keys are kept, idle keys are dropped
/// first, and when every key is active the one idle longest is evicted.
/// </summary>
public sealed class SlidingWindowRateLimiter
{
    private readonly TimeProvider _time;
    private readonly int _limit;
    private readonly long _windowTicks;
    private readonly int _maxTrackedKeys;
    private readonly Dictionary<string, Queue<long>> _hits = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    /// <summary>Creates a limiter allowing <paramref name="limit"/> hits per <paramref name="window"/> for each key.</summary>
    public SlidingWindowRateLimiter(TimeProvider time, int limit, TimeSpan window, int maxTrackedKeys = 4096)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTrackedKeys, 1);
        _time = time;
        _limit = limit;
        _windowTicks = window.Ticks;
        _maxTrackedKeys = maxTrackedKeys;
    }

    /// <summary>How many keys are currently tracked.</summary>
    public int TrackedKeys
    {
        get
        {
            lock (_lock)
                return _hits.Count;
        }
    }

    /// <summary>Records a hit for <paramref name="key"/> and returns <see langword="true"/>, or returns <see langword="false"/> when the key is at its limit.</summary>
    public bool TryAcquire(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        long now = _time.GetUtcNow().UtcTicks;
        lock (_lock)
        {
            if (!_hits.TryGetValue(key, out Queue<long>? queue))
            {
                if (_hits.Count >= _maxTrackedKeys)
                    Evict(now);
                queue = new Queue<long>();
                _hits[key] = queue;
            }
            while (queue.Count > 0 && now - queue.Peek() >= _windowTicks)
                queue.Dequeue();
            if (queue.Count >= _limit)
                return false;
            queue.Enqueue(now);
            return true;
        }
    }

    private void Evict(long now)
    {
        var idle = new List<string>();
        foreach ((string k, Queue<long> q) in _hits)
        {
            if (q.Count == 0 || now - q.Last() >= _windowTicks)
                idle.Add(k);
        }
        foreach (string k in idle)
            _hits.Remove(k);
        if (_hits.Count < _maxTrackedKeys)
            return;

        string? oldest = null;
        long oldestTicks = long.MaxValue;
        foreach ((string k, Queue<long> q) in _hits)
        {
            long last = q.Count == 0 ? 0 : q.Last();
            if (last < oldestTicks)
            {
                oldestTicks = last;
                oldest = k;
            }
        }
        if (oldest is not null)
            _hits.Remove(oldest);
    }
}
