using Bazaar.Server.Providers;
using Microsoft.Extensions.Logging;

namespace Bazaar.RateLimits.Cache;

/// <summary>
/// Sliding-window-counter limiter over a shared cache counter (doc 15, "Shared cache-backed limiter", PROPOSED). Each policy and scope key
/// has fixed buckets one window long; a call counts against the current bucket and the previous bucket is weighted by the share of the
/// window still ahead, so counts are shared by every caller and survive a restart while the cache keeps its data.
/// <para>
/// The call increments first and compares after, so concurrent callers never over-admit. A denied call undoes its increment, so a denial is
/// not counted; if the undo fails the bucket stays one too high until it expires (fails closed).
/// </para>
/// <para>
/// <see cref="IRateLimiter"/> is synchronous, so each call waits for the cache at most <see cref="CacheRateLimiterOptions.CacheTimeout"/>.
/// On a timeout, any error, or no <see cref="ICacheCounter"/>, the call is decided by an in-process <see cref="InMemoryRateLimiter"/>
/// instead: never open, never throwing. The next call tries the cache again, so recovery is automatic. One warning is logged per outage,
/// with no key, scope or policy material.
/// </para>
/// </summary>
public sealed partial class CacheRateLimiter : IRateLimiter
{
    private readonly Func<ICacheCounter?> _counterSource;
    private readonly TimeSpan _timeout;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly IRateLimiter _fallback;
    private int _degraded;

    /// <param name="counterSource">Returns the cache counter, or null when no provider offers one. Called on every call.</param>
    /// <param name="options">The cache wait. Must be valid.</param>
    /// <param name="time">Bucket indexes come from this clock. The cache wait itself always uses the real clock.</param>
    /// <param name="logger">Receives the outage and recovery lines.</param>
    /// <param name="fallback">The limiter used while the cache path is unavailable. Defaults to a new in-memory limiter.</param>
    public CacheRateLimiter(
        Func<ICacheCounter?> counterSource,
        CacheRateLimiterOptions options,
        TimeProvider time,
        ILogger logger,
        IRateLimiter? fallback = null)
    {
        ArgumentNullException.ThrowIfNull(counterSource);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        if (options.ValidationError() is { } error)
        {
            throw new ArgumentException(error, nameof(options));
        }

        _counterSource = counterSource;
        _timeout = options.CacheTimeout;
        _time = time;
        _logger = logger;
        _fallback = fallback ?? new InMemoryRateLimiter(time);
    }

    public RateLimitDecision TryAcquire(RateLimitPolicy policy, string scopeKey)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        if (policy.Limit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(policy), policy.Limit, "A rate-limit policy needs a limit of at least 1.");
        }
        if (policy.Window < TimeSpan.FromMilliseconds(1))
        {
            throw new ArgumentOutOfRangeException(nameof(policy), policy.Window, "A rate-limit window must be at least one millisecond.");
        }

        ICacheCounter? counter;
        try
        {
            counter = _counterSource();
        }
        catch (Exception ex)
        {
            return Degrade(ex.GetType().Name, policy, scopeKey);
        }

        if (counter is null)
        {
            return Degrade("no cache counter is registered", policy, scopeKey);
        }

        try
        {
            var decision = AcquireFromCache(counter, policy, scopeKey);
            if (Interlocked.Exchange(ref _degraded, 0) == 1)
            {
                LogRecovered(_logger);
            }

            return decision;
        }
        catch (Exception ex)
        {
            return Degrade(ex is TimeoutException ? "timeout" : ex.GetType().Name, policy, scopeKey);
        }
    }

    private RateLimitDecision Degrade(string reason, RateLimitPolicy policy, string scopeKey)
    {
        // The reason is a type name or a fixed phrase, never an exception message: those can echo an endpoint.
        if (Interlocked.Exchange(ref _degraded, 1) == 0)
        {
            LogUnavailable(_logger, reason);
        }

        return _fallback.TryAcquire(policy, scopeKey);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The rate-limit cache is unavailable ({Reason}). Limits are counted in this process only until it answers again.")]
    private static partial void LogUnavailable(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "The rate-limit cache answered again; shared limits are back in force.")]
    private static partial void LogRecovered(ILogger logger);

    private RateLimitDecision AcquireFromCache(ICacheCounter counter, RateLimitPolicy policy, string scopeKey)
    {
        var windowMs = (long)policy.Window.TotalMilliseconds;
        var nowMs = _time.GetUtcNow().ToUnixTimeMilliseconds();
        var bucket = nowMs / windowMs;
        var intoBucket = nowMs - (bucket * windowMs);
        var currentKey = Key(policy, windowMs, scopeKey, bucket);
        var bucketTtl = policy.Window * 2;

        // The previous bucket is read with an increment of zero; it is never incremented, only read, and its expiry is left to the cache.
        var (current, previous) = Wait(CountAsync(counter, currentKey, Key(policy, windowMs, scopeKey, bucket - 1), policy.Window, bucketTtl));

        var weightedPrevious = (long)(previous * (double)(windowMs - intoBucket) / windowMs);
        var estimate = weightedPrevious + current;
        if (estimate <= policy.Limit)
        {
            return new RateLimitDecision(Allowed: true, Remaining: (int)Math.Min(int.MaxValue, policy.Limit - estimate), RetryAfter: TimeSpan.Zero);
        }

        // Denied calls are not counted: take our increment back. A failure here leaves the bucket one high (fails closed).
        try
        {
            Wait(counter.IncrementAsync(currentKey, -1, bucketTtl, CancellationToken.None));
        }
        catch (Exception)
        {
            // Over-counted by one until the bucket expires; the decision stands.
        }

        return new RateLimitDecision(Allowed: false, Remaining: 0, RetryAfter: RetryAfter(policy, windowMs, intoBucket, previous, current - 1));
    }

    private static async Task<(long Current, long Previous)> CountAsync(
        ICacheCounter counter, string currentKey, string previousKey, TimeSpan previousTtl, TimeSpan currentTtl)
    {
        var current = counter.IncrementAsync(currentKey, 1, currentTtl, CancellationToken.None);
        var previous = counter.IncrementAsync(previousKey, 0, previousTtl, CancellationToken.None);
        await Task.WhenAll(current, previous).ConfigureAwait(false);
        return (await current.ConfigureAwait(false), Math.Max(0, await previous.ConfigureAwait(false)));
    }

    /// <summary>
    /// A hint, never below the true wait: when the current bucket alone is full the next slot can open only at the next bucket; otherwise
    /// the time until the weighted previous bucket has shrunk enough.
    /// </summary>
    private static TimeSpan RetryAfter(RateLimitPolicy policy, long windowMs, long intoBucket, long previous, long currentWithoutUs)
    {
        var room = policy.Limit - currentWithoutUs;
        long waitMs = windowMs - intoBucket;
        if (room >= 1 && previous > 0)
        {
            // Need floor(previous * (windowMs - into') / windowMs) <= room - 1, that is previous * (windowMs - into') < room * windowMs.
            var neededInto = (long)Math.Floor(windowMs - (room * (double)windowMs / previous)) + 1;
            if (neededInto < windowMs)
            {
                waitMs = neededInto - intoBucket;
            }
        }

        return TimeSpan.FromMilliseconds(Math.Max(1, waitMs));
    }

    /// <summary>Waits for the cache at most the configured timeout, on the real clock. Throws <see cref="TimeoutException"/> or the cache's own error.</summary>
    private T Wait<T>(Task<T> task)
    {
        try
        {
            return task.WaitAsync(_timeout).GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            // The abandoned call may still finish; observe its fault so it does not go unobserved.
            _ = task.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            throw;
        }
    }

    private static string Key(RateLimitPolicy policy, long windowMs, string scopeKey, long bucket) =>
        $"ratelimit:v1:{Uri.EscapeDataString(policy.Name)}:{windowMs}:{Uri.EscapeDataString(scopeKey)}:{bucket}";
}
