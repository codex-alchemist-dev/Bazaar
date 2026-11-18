using Bazaar.Vms.Environments;

namespace Bazaar.Server.AgentSafety.Containment;

/// <summary>Lists an agent's VMs, retired ones included. The pauser reads it; the Vms repository is the production source.</summary>
public interface IAgentVmDirectory
{
    Task<IReadOnlyList<EnvironmentVmRecord>> ListAsync(Guid agentId, CancellationToken ct);
}

/// <summary>The production <see cref="IAgentVmDirectory"/>, over the Vms environment repository.</summary>
public sealed class RepositoryAgentVmDirectory(Bazaar.Vms.Environments.EnvironmentRepository repository) : IAgentVmDirectory
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<EnvironmentVmRecord>> ListAsync(Guid agentId, CancellationToken ct) =>
        await repository.ListVmsForAgentAsync(agentId, ct).ConfigureAwait(false);
}
