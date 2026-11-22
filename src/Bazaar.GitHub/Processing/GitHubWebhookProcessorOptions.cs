namespace Bazaar.GitHub.Processing;

/// <summary>Operator settings for the webhook processor (doc 21, Ingress). Bound from <c>Bazaar:GitHub:Processor</c>.</summary>
public sealed class GitHubWebhookProcessorOptions
{
    /// <summary>Most rows claimed per pass.</summary>
    public int BatchSize { get; init; } = 20;

    /// <summary>How long a claim holds a row. A crashed instance's rows become claimable again after this.</summary>
    public TimeSpan ClaimLease { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Sleep when a pass found nothing to do.</summary>
    public TimeSpan IdleDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>First retry wait after a sink failure; doubles per failed attempt up to <see cref="MaxRetryBackoff"/>.</summary>
    public TimeSpan RetryBackoffBase { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan MaxRetryBackoff { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Bound on remembered per-PR applied state (per instance, in memory).</summary>
    public int MaxTrackedPullRequests { get; init; } = 10_000;

    /// <summary>The wait before the next try after <paramref name="attempts"/> failed attempts (1 for the first failure).</summary>
    public TimeSpan BackoffFor(int attempts)
    {
        var ticks = RetryBackoffBase.Ticks * Math.Pow(2, Math.Clamp(attempts - 1, 0, 20));
        return TimeSpan.FromTicks((long)Math.Min(ticks, MaxRetryBackoff.Ticks));
    }
}
