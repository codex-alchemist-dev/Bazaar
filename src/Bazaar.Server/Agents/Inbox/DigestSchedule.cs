namespace Bazaar.Server.Agents.Inbox;

/// <summary>When the digest is next due (doc 06: every 2 simulated working hours and at shift start).</summary>
public static class DigestSchedule
{
    /// <summary>
    /// True when no digest has run yet, or when at least <paramref name="interval"/> has passed since the last one.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="interval"/> is not positive.</exception>
    public static bool IsDue(DateTimeOffset? lastDigestAt, DateTimeOffset now, TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), interval, "The digest interval must be positive.");
        }

        return lastDigestAt is null || now - lastDigestAt.Value >= interval;
    }
}
