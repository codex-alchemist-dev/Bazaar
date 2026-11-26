namespace Bazaar.Server.Vms.Abstractions;

/// <summary>
/// Read port for other modules: which VMs an agent has and which VMs are running. It reads the VM module's records and calls no
/// backend. This is not <c>Bazaar.Server.AgentSafety.Containment.IAgentVmDirectory</c>, which returns the VM module's own records
/// to the agent pauser.
/// </summary>
public interface IAgentVmDirectory
{
    /// <summary>Lists the agent's VMs that are not retired, oldest first. An agent with none gets an empty list.</summary>
    /// <param name="agentId">The agent.</param>
    /// <param name="ct">Cancellation.</param>
    Task<IReadOnlyList<AgentVmInfo>> ListForAgentAsync(Guid agentId, CancellationToken ct);

    /// <summary>Lists every VM recorded as running, of any agent, oldest first. Each entry has a non-null backend reference.</summary>
    /// <param name="ct">Cancellation.</param>
    Task<IReadOnlyList<AgentVmInfo>> ListRunningAsync(CancellationToken ct);
}
