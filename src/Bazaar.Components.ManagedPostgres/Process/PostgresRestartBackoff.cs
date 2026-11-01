namespace Bazaar.Components.ManagedPostgres;

/// <summary>Exponential restart delay with a ceiling. Attempt 1 waits the base delay, each later attempt doubles it, and the result never exceeds the maximum.</summary>
public static class PostgresRestartBackoff
{
    private const int MaxDoublings = 30;

    /// <summary>Delay before restart attempt <paramref name="attempt"/> (1-based).</summary>
    public static TimeSpan DelayFor(int attempt, TimeSpan baseDelay, TimeSpan maxDelay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);

        var doublings = Math.Min(attempt - 1, MaxDoublings);
        var ticks = baseDelay.Ticks * Math.Pow(2, doublings);
        return TimeSpan.FromTicks((long)Math.Min(ticks, maxDelay.Ticks));
    }
}
