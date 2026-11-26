using Bazaar.Server.RateLimits;

namespace Bazaar.Server.GitHub.Tools;

/// <summary>
/// Sliding-window limiter used by the agent GitHub tools when the host registered no <see cref="IRateLimiter"/>. Per process, in
/// memory, keyed by policy name and scope key; a denied call is not counted. Time comes from the injected <see cref="TimeProvider"/>.
/// </summary>
internal sealed class GitHubInMemoryRateLimiter(TimeProvider time) : IRateLimiter
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string Policy, string Key), Queue<DateTimeOffset>> _buckets = [];

    public RateLimitDecision TryAcquire(RateLimitPolicy policy, string scopeKey)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKey);
        lock (_gate)
        {
            var now = time.GetUtcNow();
            if (!_buckets.TryGetValue((policy.Name, scopeKey), out var log))
            {
                log = new Queue<DateTimeOffset>();
                _buckets.Add((policy.Name, scopeKey), log);
            }

            while (log.Count > 0 && now - log.Peek() >= policy.Window)
            {
                log.Dequeue();
            }

            if (log.Count >= policy.Limit)
            {
                return new RateLimitDecision(false, 0, log.Peek() + policy.Window - now);
            }

            log.Enqueue(now);
            return new RateLimitDecision(true, policy.Limit - log.Count, TimeSpan.Zero);
        }
    }
}
