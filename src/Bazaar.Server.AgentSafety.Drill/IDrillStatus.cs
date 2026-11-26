namespace Bazaar.Server.AgentSafety.Drill;

/// <summary>Read-only view of the drill for the status page and, later, Kodo. Reading it is always allowed.</summary>
public interface IDrillStatus
{
    /// <summary>The most recent drill run, or null when none has run.</summary>
    Task<DrillResult?> GetLatestAsync(CancellationToken ct = default);
}

/// <summary>The <see cref="IDrillStatus"/> that reads the latest run from the result store.</summary>
/// <param name="store">The store that holds the runs.</param>
public sealed class DrillStatus(IDrillResultStore store) : IDrillStatus
{
    /// <inheritdoc />
    public Task<DrillResult?> GetLatestAsync(CancellationToken ct = default) => store.LatestAsync(ct);
}
