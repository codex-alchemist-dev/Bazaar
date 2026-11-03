namespace Bazaar.Server.Memory.Consolidation;

/// <summary>
/// Per-agent advisory lock for consolidation: at most one run per agent at a time. The lease is held until
/// it is disposed.
/// </summary>
public interface IConsolidationLock
{
    /// <summary>Takes the agent's lock. Returns null when another run already holds it.</summary>
    Task<IDisposable?> TryAcquireAsync(string agentId, CancellationToken ct);
}
