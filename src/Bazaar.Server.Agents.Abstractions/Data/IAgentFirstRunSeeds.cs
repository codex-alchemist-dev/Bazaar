namespace Bazaar.Server.Agents.Data;

/// <summary>
/// First-run seeding seams for agents (doc 45, section 1.15a, PROPOSED; the lifecycle is doc 06, "Lifecycle"). A seed creates
/// an agent on hold, so the automatic Created to Onboarding step does not run until the seed releases it. A marker names the
/// seed, so a repeated run finds the same agent instead of creating another.
/// </summary>
public interface IAgentFirstRunSeeds
{
    /// <summary>
    /// Creates an agent in <see cref="AgentState.Created"/> with its hold set, and returns its id. <paramref name="marker"/> must
    /// be unique across agents. A marker that is already in use throws <see cref="InvalidOperationException"/>; call
    /// <see cref="FindByMarkerAsync"/> first to reuse the existing agent.
    /// </summary>
    Task<Guid> CreateHeldAsync(string name, string marker, Guid? soulVersionId = null, CancellationToken ct = default);

    /// <summary>
    /// Clears the hold so the automatic step can run. Returns false when the agent does not exist or is not held. Releasing an
    /// agent twice is safe.
    /// </summary>
    Task<bool> ReleaseHoldAsync(Guid agentId, CancellationToken ct = default);

    /// <summary>
    /// The automatic Created to Onboarding step (doc 06, "Lifecycle"). Moves the agent to <see cref="AgentState.Onboarding"/> only
    /// when it is in <see cref="AgentState.Created"/> and not held. Returns false otherwise, and the agent stays where it is.
    /// </summary>
    Task<bool> TryStartAutomaticOnboardingAsync(Guid agentId, CancellationToken ct = default);

    /// <summary>Returns the id of the agent created with <paramref name="marker"/>, or null when there is none.</summary>
    Task<Guid?> FindByMarkerAsync(string marker, CancellationToken ct = default);

    /// <summary>
    /// Deletes the agent only when it never started a turn. Pending inbox items for it are deleted with it. Returns false, and
    /// deletes nothing, when the agent has a turn or does not exist.
    /// </summary>
    Task<bool> RemoveUnstartedAsync(Guid agentId, CancellationToken ct = default);
}
