namespace Bazaar.Server.Agents.Data;

/// <summary>The <see cref="IAgentDefaultPolicyGrant"/> used when the composition root binds none. It grants nothing.</summary>
public sealed class NoAgentDefaultPolicyGrant : IAgentDefaultPolicyGrant
{
    /// <summary>The shared instance.</summary>
    public static readonly NoAgentDefaultPolicyGrant Instance = new();

    /// <inheritdoc />
    public Task GrantAsync(Guid agentId, CancellationToken ct) => Task.CompletedTask;
}
