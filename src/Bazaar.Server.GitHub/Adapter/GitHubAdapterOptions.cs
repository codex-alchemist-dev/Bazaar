namespace Bazaar.Server.GitHub.Adapter;

/// <summary>
/// Tuning for <see cref="GitHubAdapter"/> (doc 21, Rate limits and Retries). Defaults match the design; the adapter
/// never loosens the warn and defer thresholds below the doc's values because those are operator-visible limits.
/// </summary>
public sealed class GitHubAdapterOptions
{
    /// <summary>GitHub REST root. Must be absolute https. GitHub Enterprise uses its own <c>/api/v3</c> root.</summary>
    public Uri BaseAddress { get; init; } = new("https://api.github.com/");

    /// <summary>Retries after the first attempt, for 5xx, transport failures and rate-limit waits. Default 3 (1s, 2s, 4s).</summary>
    public int MaxRetries { get; init; } = 3;

    /// <summary>Base of the exponential backoff for 5xx and transport failures.</summary>
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Longest rate-limit wait the adapter sleeps through. Longer waits surface as <see cref="GitHubRateLimitedException"/>.</summary>
    public TimeSpan MaxInlineRateLimitDelay { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Wait used for a 429 that carries neither <c>retry-after</c> nor a reset header (GitHub asks for at least one minute).</summary>
    public TimeSpan RateLimitFallbackDelay { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Core-limit usage at which a warning is logged (doc 21: 70%).</summary>
    public double WarnUsageRatio { get; init; } = 0.7;

    /// <summary>Core-limit usage at which reads are deferred until the window resets (doc 21: 90%).</summary>
    public double DeferUsageRatio { get; init; } = 0.9;

    /// <summary>Upper bound on cached GET bodies. The oldest-inserted entry is evicted when full.</summary>
    public int MaxCacheEntries { get; init; } = 1024;
}
