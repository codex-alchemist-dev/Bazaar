namespace Bazaar.Server.Components.RateLimits;

/// <summary>
/// Outcome of one <see cref="IRateLimiter.TryAcquire"/> call.
/// </summary>
/// <param name="Allowed">True when the event was admitted and counted.</param>
/// <param name="Remaining">Events still admissible in the current window after this call (0 when denied).</param>
/// <param name="RetryAfter">When denied, the time until the oldest counted event leaves the window and a slot frees.
/// <see cref="TimeSpan.Zero"/> when allowed.</param>
public readonly record struct RateLimitDecision(bool Allowed, int Remaining, TimeSpan RetryAfter);

/// <summary>
/// Admission control for side-effecting capabilities (doc 15, C36). A denied call is not counted, so retries do not
/// extend the penalty.
/// </summary>
public interface IRateLimiter
{
    /// <summary>
    /// Counts one event against <paramref name="policy"/> for <paramref name="scopeKey"/> if the window has room.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="scopeKey"/> is null, empty or whitespace.</exception>
    RateLimitDecision TryAcquire(RateLimitPolicy policy, string scopeKey);
}
