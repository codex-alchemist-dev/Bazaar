namespace Bazaar.Server.Providers;

/// <summary>
/// Runtime side of the service host role. Install and recovery setup live in the admin CLI, not here.
/// Host builder registration (<c>AddWindowsService</c>, <c>UseSystemd</c>) lands with the host package (WP4, WP8, WP9).
/// </summary>
public interface IServiceHostProvider : IProvider
{
    /// <summary>Reads back the restart policy the service manager applies, so startup can report it.</summary>
    Task<ServiceRecoveryReport> RecoveryAsync(CancellationToken ct);
}

/// <summary>What the service manager will do after a crash. <paramref name="Configured"/> is false when no policy is set.</summary>
public sealed record ServiceRecoveryReport(bool Configured, IReadOnlyList<string> Actions);
