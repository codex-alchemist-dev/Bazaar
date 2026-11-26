using Bazaar.Server.Gateway.Data;

namespace Bazaar.Server.Gateway.SystemPolicy;

/// <summary>The seam between the seeder and <c>agent_model_policy</c>, so the seeder tests need no database.</summary>
public interface ISystemPolicyStore
{
    Task<AgentModelPolicyRow?> GetRowAsync(string agentId, CancellationToken cancellationToken);

    Task SaveAsync(string agentId, IReadOnlyList<string> entries, string? defaultRoute, string changedBy, CancellationToken cancellationToken);
}

/// <summary>Adapts <see cref="AgentModelPolicyRepository"/>; every save goes through its validating <c>SaveAsync</c>.</summary>
public sealed class RepositorySystemPolicyStore(AgentModelPolicyRepository repository) : ISystemPolicyStore
{
    public Task<AgentModelPolicyRow?> GetRowAsync(string agentId, CancellationToken cancellationToken) =>
        repository.GetRowAsync(agentId, cancellationToken);

    public Task SaveAsync(string agentId, IReadOnlyList<string> entries, string? defaultRoute, string changedBy, CancellationToken cancellationToken) =>
        repository.SaveAsync(agentId, entries, defaultRoute, changedBy, cancellationToken);
}
