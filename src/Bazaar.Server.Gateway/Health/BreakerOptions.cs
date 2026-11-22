namespace Bazaar.Server.Gateway.Health;

/// <summary>
/// Tuning for the health breakers and the active-probe cadence. The defaults are the shipped safe values.
/// <c>ProbeInterval</c> is read by callers that schedule probes; this package does not run a scheduler.
/// </summary>
public sealed record BreakerOptions
{
    /// <summary>Consecutive health failures that open a closed breaker.</summary>
    public int FailureThreshold { get; init; } = 5;

    /// <summary>How long an open breaker rejects calls before it admits half-open trials.</summary>
    public TimeSpan OpenDuration { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Trial calls a half-open breaker admits at once.</summary>
    public int HalfOpenMaxTrials { get; init; } = 1;

    /// <summary>Successful trials needed to close a half-open breaker.</summary>
    public int SuccessesToClose { get; init; } = 1;

    /// <summary>How often a caller should run an active probe for a provider key.</summary>
    public TimeSpan ProbeInterval { get; init; } = TimeSpan.FromSeconds(60);
}
