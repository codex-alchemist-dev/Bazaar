using System.Collections.Concurrent;

namespace Bazaar.Server.Http;

/// <summary>
/// Token bucket per principal for the HTTP surface. A distinct type with its own state, so wire traffic can never consume it
/// (doc 02 WP10: separate rate bucket).
/// </summary>
public sealed class HttpRateLimiter(HttpSurfaceOptions options, TimeProvider time)
{
    private const int _pruneAbove = 10_000;

    private readonly ConcurrentDictionary<Guid, Bucket> _buckets = new();

    private sealed class Bucket(double tokens, DateTimeOffset stamp)
    {
        public double Tokens = tokens;
        public DateTimeOffset Stamp = stamp;
    }

    /// <summary>Takes one token for <paramref name="principal"/>; false means rate limited, with the seconds to wait.</summary>
    public bool TryAcquire(Guid principal, out int retryAfterSeconds)
    {
        var now = time.GetUtcNow();
        var bucket = _buckets.GetOrAdd(principal, _ => new Bucket(options.RequestBurst, now));
        lock (bucket)
        {
            var seconds = (now - bucket.Stamp).TotalSeconds;
            bucket.Tokens = Math.Min(options.RequestBurst, bucket.Tokens + (seconds * options.RequestsPerSecond));
            bucket.Stamp = now;
            if (bucket.Tokens >= 1)
            {
                bucket.Tokens -= 1;
                retryAfterSeconds = 0;
                Prune();
                return true;
            }

            retryAfterSeconds = Math.Max(1, (int)Math.Ceiling((1 - bucket.Tokens) / options.RequestsPerSecond));
            return false;
        }
    }

    private void Prune()
    {
        if (_buckets.Count <= _pruneAbove) return;
        var now = time.GetUtcNow();
        foreach (var (key, bucket) in _buckets)
        {
            double tokens;
            lock (bucket) tokens = bucket.Tokens + ((now - bucket.Stamp).TotalSeconds * options.RequestsPerSecond);
            if (tokens >= options.RequestBurst) _buckets.TryRemove(key, out _);
        }
    }
}
