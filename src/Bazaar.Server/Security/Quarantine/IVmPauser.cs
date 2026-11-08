namespace Bazaar.Server.Security.Quarantine;

/// <summary>Pauses both VMs of an agent (doc 16; kill switch).</summary>
public interface IVmPauser
{
    /// <summary>Pauses the agent's Windows and Linux VMs. Must be idempotent.</summary>
    Task PauseAsync(string agentId, CancellationToken ct = default);
}
