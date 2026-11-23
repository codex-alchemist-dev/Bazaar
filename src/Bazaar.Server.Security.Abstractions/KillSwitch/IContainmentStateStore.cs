namespace Bazaar.Server.Security.KillSwitch;

/// <summary>One contained agent, as the containment store keeps it.</summary>
/// <param name="AgentId">The contained agent.</param>
/// <param name="ContainedAt">When the agent was contained, from the adapter's <see cref="TimeProvider"/>.</param>
/// <param name="ActorId">The actor recorded for the containment.</param>
public sealed record ContainedAgent(string AgentId, DateTimeOffset ContainedAt, string ActorId);

/// <summary>
/// Durable set of contained agents (migration agent_safety 0003). <see cref="AgentContainmentAdapter"/> adds an agent before it
/// pauses and revokes it, removes it only after the release has finished, and loads the set when it starts, so a restarted
/// host still knows which agents to release.
/// </summary>
/// <remarks>
/// <see cref="AddAsync"/> keeps the first record for an agent that is already contained. A write that throws must leave the
/// stored set as it was.
/// </remarks>
public interface IContainmentStateStore
{
    /// <summary>Reads every contained agent.</summary>
    Task<IReadOnlyList<ContainedAgent>> LoadAsync(CancellationToken ct = default);

    /// <summary>Stores a contained agent. Keeps the existing record when the agent is already stored.</summary>
    Task AddAsync(ContainedAgent agent, CancellationToken ct = default);

    /// <summary>Removes a contained agent. Does nothing when it is not stored.</summary>
    Task RemoveAsync(string agentId, CancellationToken ct = default);
}
