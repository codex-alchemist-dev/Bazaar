using System.Collections.Concurrent;

namespace Bazaar.Server.Athanor;

/// <summary>Sliding one-minute window of saves per user (<c>athanor.save_rate_per_min</c>).</summary>
public sealed class SaveRateLimiter
{
    private static readonly TimeSpan _window = TimeSpan.FromMinutes(1);

    private readonly IAthanorLimits _limits;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _hits = new(StringComparer.Ordinal);

    public SaveRateLimiter(IAthanorLimits limits, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(time);
        _limits = limits;
        _time = time;
    }

    /// <summary>Records an attempt when the user is under the limit. Otherwise returns false and how long until a slot frees.</summary>
    public bool TryAcquire(string userId, out long retryAfterMs)
    {
        var now = _time.GetUtcNow();
        var queue = _hits.GetOrAdd(userId, static _ => new Queue<DateTimeOffset>());
        lock (queue)
        {
            while (queue.Count > 0 && now - queue.Peek() >= _window)
            {
                queue.Dequeue();
            }

            if (queue.Count >= _limits.SaveRatePerMinute)
            {
                retryAfterMs = (long)Math.Ceiling((queue.Peek() + _window - now).TotalMilliseconds);
                return false;
            }

            queue.Enqueue(now);
            retryAfterMs = 0;
            return true;
        }
    }
}
