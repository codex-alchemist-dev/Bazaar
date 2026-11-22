using System.Collections.Concurrent;

namespace Bazaar.Server.GcReactions;

/// <summary>
/// A per-key sliding window: at most <c>limit</c> hits per window. In memory, so a restart forgets it; that only ever loosens a
/// limit for one window, and the limit protects the log from floods, not an invariant.
/// </summary>
public sealed class SlidingRateLimiter(TimeProvider time, TimeSpan window)
{
    private readonly ConcurrentDictionary<(Guid Gc, Guid Who), Queue<DateTimeOffset>> _hits = new();
    private int _calls;

    /// <summary>Takes one hit. False (with the wait until a hit frees up) when the key already used <paramref name="limit"/> in the window.</summary>
    public bool TryAcquire(Guid gcId, Guid principalId, int limit, out TimeSpan retryAfter)
    {
        var now = time.GetUtcNow();
        var queue = _hits.GetOrAdd((gcId, principalId), _ => new Queue<DateTimeOffset>());
        lock (queue)
        {
            while (queue.Count > 0 && now - queue.Peek() >= window)
                queue.Dequeue();
            if (queue.Count >= limit)
            {
                retryAfter = window - (now - queue.Peek());
                return false;
            }

            queue.Enqueue(now);
        }

        if (Interlocked.Increment(ref _calls) % 256 == 0)
            Sweep(now);
        retryAfter = TimeSpan.Zero;
        return true;
    }

    private void Sweep(DateTimeOffset now)
    {
        foreach (var (key, queue) in _hits)
        {
            lock (queue)
            {
                if (queue.Count == 0 || now - queue.Last() >= window)
                    _hits.TryRemove(key, out _);
            }
        }
    }
}
