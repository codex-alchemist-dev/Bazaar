namespace Bazaar.Server.Providers;

/// <summary>
/// An optional, narrow capability of a cache provider: an atomic counter with a time to live (doc 03, DECISIONS-LOG round 37, PROPOSED).
/// It is separate from <see cref="ICacheProvider"/> so a provider that cannot count atomically does not have to pretend. Cache only:
/// counts are not durable, and the platform works with the cache down.
/// <para>
/// Unlike <see cref="ICacheProvider"/>, which degrades quietly, a counter call throws when the cache cannot be reached or answers
/// with an error. A caller that enforces a limit must see an outage in order to fall back safely.
/// </para>
/// </summary>
public interface ICacheCounter
{
    /// <summary>
    /// Adds <paramref name="by"/> (which may be zero or negative) to the counter at <paramref name="key"/> and returns the new value.
    /// A missing key counts as zero. The time to live is set only when the key has none, in the same atomic step as the increment,
    /// so a counter never lives without an expiry and a later call never extends it.
    /// </summary>
    Task<long> IncrementAsync(string key, long by, TimeSpan ttl, CancellationToken ct);
}
