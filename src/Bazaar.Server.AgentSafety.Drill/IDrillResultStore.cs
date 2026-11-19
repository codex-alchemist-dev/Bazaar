namespace Bazaar.Server.AgentSafety.Drill;

/// <summary>
/// Durable record of drill runs (agent_safety migration 0005). Runs are appended and never edited. The drill result is written after
/// the run, so a run that is cancelled leaves no record.
/// </summary>
public interface IDrillResultStore
{
    /// <summary>Appends one run.</summary>
    Task AddAsync(DrillResult result, CancellationToken ct = default);

    /// <summary>The most recent run by start time, or null when no run is stored.</summary>
    Task<DrillResult?> LatestAsync(CancellationToken ct = default);
}
