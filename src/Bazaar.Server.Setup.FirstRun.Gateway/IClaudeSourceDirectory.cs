using Bazaar.Server.Gateway.Policy;

namespace Bazaar.Server.Setup.FirstRun.Gateway;

/// <summary>One model the gateway offers from an enabled source, in the Claude family.</summary>
/// <param name="Model">The provider-qualified model.</param>
/// <param name="Native">True when the source is the owner's own key; false for a pooled or other source. Native sorts first (doc 05, <c>native_first</c>).</param>
public sealed record ClaudeSourceModel(ModelRef Model, bool Native);

/// <summary>
/// What the first-run seed needs from the gateway directory (doc 45 section 1.15): the Claude models of enabled sources, and a
/// place to write the default agent's route. The Postgres-backed adapter lives in the host composition; tests use a fake.
/// </summary>
public interface IClaudeSourceDirectory
{
    /// <summary>Every available Claude-family model of every enabled source. Disabled sources and missing models are left out.</summary>
    Task<IReadOnlyList<ClaudeSourceModel>> ListClaudeModelsAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates or replaces the priority route <paramref name="routeId"/> over <paramref name="members"/>, in order.</summary>
    Task UpsertPriorityRouteAsync(string routeId, IReadOnlyList<ModelRef> members, CancellationToken cancellationToken = default);
}

/// <summary>Writes the default agent's model policy, never over an edit the operator made.</summary>
public interface IDefaultAgentPolicyStore
{
    /// <summary>The label of the row's last writer, or null when the agent has no policy row.</summary>
    Task<string?> GetChangedByAsync(string agentId, CancellationToken cancellationToken = default);

    /// <summary>Replaces the agent's policy with <paramref name="entries"/> and <paramref name="defaultRoute"/>, labelled <paramref name="changedBy"/>.</summary>
    Task SaveAsync(string agentId, IReadOnlyList<string> entries, string? defaultRoute, string changedBy, CancellationToken cancellationToken = default);
}
