namespace Bazaar.Server.Gateway.SystemPolicy;

/// <summary>
/// What a module wants one system agent to be allowed (docs/design/05-model-gateway.md, "System-agent policy seeding"):
/// provider-qualified models and route names, plus the default route. The seeder validates the entries the same way
/// every policy is validated, so a wildcard or <c>auto</c> entry is rejected there, not here.
/// </summary>
/// <param name="AgentId">The system agent's caller id, for example <c>bazaar-embeddings</c>.</param>
/// <param name="Entries">The allowed entries, in order.</param>
/// <param name="DefaultRoute">The route or model used when a request names none; one of <paramref name="Entries"/>.</param>
public sealed record SystemAgentPolicyDeclaration(string AgentId, IReadOnlyList<string> Entries, string? DefaultRoute);

/// <summary>
/// A module's declaration of one system agent. The gateway module never names a caller; modules register sources and the
/// seeder reconciles them. <see cref="DeclareAsync"/> builds the declaration from the current settings each time it is called.
/// </summary>
public interface ISystemAgentPolicySource
{
    /// <summary>The system agent this source declares.</summary>
    string AgentId { get; }

    /// <summary>
    /// The declaration for the current settings, or null when the agent is not configured, in which case no row is written.
    /// A source that cannot resolve a name it needs also returns null and logs why.
    /// </summary>
    ValueTask<SystemAgentPolicyDeclaration?> DeclareAsync(CancellationToken cancellationToken = default);
}
