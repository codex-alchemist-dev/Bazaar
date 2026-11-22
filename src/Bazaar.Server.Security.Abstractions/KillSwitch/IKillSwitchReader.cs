namespace Bazaar.Server.Security.KillSwitch;

/// <summary>
/// Read-only view of the kill switch for modules that cannot reference the server (doc 15, "Reads for modules"). It never
/// engages or resumes a stop. <c>KillSwitchEngine</c> implements it.
/// </summary>
public interface IKillSwitchReader
{
    /// <summary>
    /// True when the agent is frozen: an agent stop is engaged for it, the global stop is engaged, or the stop state is not
    /// loaded yet (fail closed). Under a global stop this is stricter than <see cref="EvaluateAsync"/>, which still allows reads.
    /// </summary>
    /// <param name="agentId">The agent, as the kill switch identifies it. Must not be blank.</param>
    /// <param name="ct">Cancels the read.</param>
    ValueTask<bool> IsFrozenAsync(string agentId, CancellationToken ct = default);

    /// <summary>The kill switch's decision for one effect, the same answer the dispatch path gets.</summary>
    /// <param name="agentId">The agent, as the kill switch identifies it. Must not be blank.</param>
    /// <param name="effect">The effect class of the action.</param>
    /// <param name="ct">Cancels the read.</param>
    ValueTask<KillSwitchDecision> EvaluateAsync(string agentId, ToolEffect effect, CancellationToken ct = default);
}
