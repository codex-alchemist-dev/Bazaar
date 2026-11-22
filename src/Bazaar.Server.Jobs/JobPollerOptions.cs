namespace Bazaar.Server.Jobs;

/// <summary>Poller timing and retry limits. Operator settings for these are registered with the settings registry (doc 03).</summary>
public sealed record JobPollerOptions
{
    /// <summary>How often the poller looks for due jobs.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a claimed job is leased before another poller may take it over.</summary>
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Most jobs claimed in one poll.</summary>
    public int BatchSize { get; init; } = 10;

    /// <summary>Claims allowed per job. A job that fails or loses its lease on the last attempt is marked failed.</summary>
    public int MaxAttempts { get; init; } = 5;

    /// <summary>Delay before the first retry. Each later retry doubles it.</summary>
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Upper bound on the retry delay.</summary>
    public TimeSpan RetryMaxDelay { get; init; } = TimeSpan.FromHours(1);

    /// <summary>Throws when a value is out of range. Called by <see cref="JobPoller"/> on construction.</summary>
    public void Validate()
    {
        if (PollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(PollInterval), PollInterval, "Poll interval must be positive.");
        if (LeaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(LeaseDuration), LeaseDuration, "Lease duration must be positive.");
        if (BatchSize < 1)
            throw new ArgumentOutOfRangeException(nameof(BatchSize), BatchSize, "Batch size must be at least 1.");
        if (MaxAttempts < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxAttempts), MaxAttempts, "Max attempts must be at least 1.");
        if (RetryBaseDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(RetryBaseDelay), RetryBaseDelay, "Retry base delay cannot be negative.");
        if (RetryMaxDelay < RetryBaseDelay)
            throw new ArgumentOutOfRangeException(nameof(RetryMaxDelay), RetryMaxDelay, "Retry max delay cannot be below the base delay.");
    }

    /// <summary>Retry delay after the given failed attempt (1 for the first failure): base times 2^(attempts-1), capped.</summary>
    public TimeSpan RetryDelayFor(int attempts)
    {
        if (attempts < 1)
            throw new ArgumentOutOfRangeException(nameof(attempts), attempts, "Attempts start at 1.");
        if (RetryBaseDelay == TimeSpan.Zero)
            return TimeSpan.Zero;
        // Cap the exponent first so the multiplication cannot overflow.
        var doublings = Math.Min(attempts - 1, 30);
        var scaled = RetryBaseDelay.TotalSeconds * Math.Pow(2, doublings);
        return TimeSpan.FromSeconds(Math.Min(scaled, RetryMaxDelay.TotalSeconds));
    }
}
