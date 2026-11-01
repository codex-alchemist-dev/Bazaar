namespace Bazaar.Server.Components.RateLimits;

/// <summary>
/// Sliding-window log limiter held in process memory. Each bucket keeps the timestamps of its admitted events, so at most
/// <see cref="RateLimitPolicy.Limit"/> entries exist per bucket. Time comes from the injected <see cref="TimeProvider"/>,
/// which keeps the behaviour deterministic under test.
/// </summary>
/// <remarks>
/// Counters are not shared across processes or restarts. The durable <c>rate_bucket</c> counters that doc 15 owns are a
/// separate concern and are not implemented here. Platform-neutral: no OS-specific APIs.
/// </remarks>
public sealed class InMemoryRateLimiter : IRateLimiter
{
    /// <summary>Idle buckets are swept after this many calls, so keys that stop being used do not accumulate forever.</summary>
    private const int SweepEveryCalls = 1024;

    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Dictionary<BucketKey, Bucket> _buckets = new();
    private int _callsSinceSweep;

    public InMemoryRateLimiter(TimeProvider time)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public RateLimitDecision TryAcquire(RateLimitPolicy policy, string scopeKey)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        if (policy.Limit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(policy), policy.Limit, "A rate-limit policy needs a limit of at least 1.");
        }
        if (policy.Window <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(policy), policy.Window, "A rate-limit window must be positive.");
        }

        var key = new BucketKey(policy.Name, scopeKey);
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            MaybeSweep(now);

            if (!_buckets.TryGetValue(key, out var bucket))
            {
                bucket = new Bucket();
                _buckets.Add(key, bucket);
            }

            // The window comes from the policy passed in, so a changed window applies on the next call.
            bucket.Window = policy.Window;
            bucket.Prune(now);

            if (bucket.Count < policy.Limit)
            {
                bucket.Admitted.Enqueue(now);
                return new RateLimitDecision(Allowed: true, Remaining: policy.Limit - bucket.Count, RetryAfter: TimeSpan.Zero);
            }

            // Full: the oldest admitted event leaves the window at oldest + window. Nothing is recorded for a denial.
            var retryAfter = bucket.Admitted.Peek() + policy.Window - now;
            if (retryAfter < TimeSpan.Zero)
            {
                retryAfter = TimeSpan.Zero;
            }
            return new RateLimitDecision(Allowed: false, Remaining: 0, RetryAfter: retryAfter);
        }
    }

    /// <summary>Drops buckets whose events have all left their window. Caller holds <see cref="_gate"/>.</summary>
    private void MaybeSweep(DateTimeOffset now)
    {
        _callsSinceSweep++;
        if (_callsSinceSweep < SweepEveryCalls)
        {
            return;
        }
        _callsSinceSweep = 0;

        List<BucketKey>? idle = null;
        foreach (var (key, bucket) in _buckets)
        {
            bucket.Prune(now);
            if (bucket.Count == 0)
            {
                (idle ??= []).Add(key);
            }
        }
        if (idle is not null)
        {
            foreach (var key in idle)
            {
                _buckets.Remove(key);
            }
        }
    }

    private readonly record struct BucketKey(string PolicyName, string ScopeKey);

    private sealed class Bucket
    {
        public TimeSpan Window { get; set; }

        /// <summary>Admission times, oldest first. Only events inside the window are kept after <see cref="Prune"/>.</summary>
        public Queue<DateTimeOffset> Admitted { get; } = new();

        public int Count => Admitted.Count;

        public void Prune(DateTimeOffset now)
        {
            var cutoff = now - Window;
            while (Admitted.Count > 0 && Admitted.Peek() <= cutoff)
            {
                Admitted.Dequeue();
            }
        }
    }
}
