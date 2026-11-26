namespace Bazaar.Server.Security.Policy;

/// <summary>A fixed-window quota: at most <see cref="Limit"/> calls in each <see cref="Window"/>.</summary>
public sealed class RateRule
{
    /// <param name="limit">Calls allowed per window. Must be at least 1.</param>
    /// <param name="window">Length of one window. Must be positive.</param>
    public RateRule(int limit, TimeSpan window)
    {
        if (limit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "A rate limit must allow at least one call.");
        }

        if (window <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(window), window, "A rate window must be positive.");
        }

        Limit = limit;
        Window = window;
    }

    /// <summary>Calls allowed per window.</summary>
    public int Limit { get; }

    /// <summary>Length of one window.</summary>
    public TimeSpan Window { get; }
}

/// <summary>Result of one quota check. <see cref="Count"/> is the calls counted in the current window, including this one when allowed.</summary>
/// <param name="Allowed">False when the window is already full. A refused check does not count.</param>
/// <param name="Count">Calls counted in the current window.</param>
public readonly record struct RateCheck(bool Allowed, int Count);

/// <summary>
/// Runtime counters for quotas (doc 15, <c>rate_bucket</c>). Keys are opaque to the store; the policy engine builds them
/// from agent, platform and tool, so one agent's quota never counts against another's.
/// </summary>
public interface IRateBucketStore
{
    /// <summary>Counts one call against <paramref name="key"/> if the window has room. Atomic per key.</summary>
    RateCheck TryConsume(string key, RateRule rule, DateTimeOffset now);
}

/// <summary>
/// Process-local fixed-window counters. Counts reset when the process restarts; the Postgres store (the
/// <c>rate_bucket</c> table) is the durable follow-up. Thread-safe.
/// </summary>
public sealed class InMemoryRateBucketStore : IRateBucketStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (DateTimeOffset WindowStart, int Count)> _buckets = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public RateCheck TryConsume(string key, RateRule rule, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(rule);

        lock (_gate)
        {
            var (start, count) = _buckets.TryGetValue(key, out var existing) && now - existing.WindowStart < rule.Window
                ? existing
                : (now, 0);

            if (count >= rule.Limit)
            {
                _buckets[key] = (start, count);
                return new RateCheck(false, count);
            }

            count++;
            _buckets[key] = (start, count);
            return new RateCheck(true, count);
        }
    }
}
