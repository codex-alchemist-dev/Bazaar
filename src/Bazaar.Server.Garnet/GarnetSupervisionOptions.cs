namespace Bazaar.Server.Garnet;

/// <summary>
/// How the host starts and restarts a managed Garnet child. Bound from <see cref="SectionName"/>. Only used when
/// <see cref="GarnetCacheOptions.Managed"/> is true.
/// </summary>
public sealed class GarnetSupervisionOptions
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "Bazaar:Cache:Garnet:Supervision";

    /// <summary>How long a freshly started child may take to answer PING before it is treated as failed.</summary>
    public TimeSpan StartupTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>First wait after a failed start or crash. Each further failure doubles it, up to <see cref="BackoffMax"/>.</summary>
    public TimeSpan BackoffBase { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Upper bound on the restart wait.</summary>
    public TimeSpan BackoffMax { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Failures inside <see cref="FailureWindow"/> that open the circuit breaker. The child is then left stopped.</summary>
    public int MaxFailures { get; set; } = 5;

    /// <summary>The window that <see cref="MaxFailures"/> counts over.</summary>
    public TimeSpan FailureWindow { get; set; } = TimeSpan.FromMinutes(10);
}
