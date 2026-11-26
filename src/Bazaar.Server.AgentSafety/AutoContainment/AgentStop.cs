using Bazaar.Server.Security.KillSwitch;

namespace Bazaar.Server.AgentSafety.AutoContainment;

/// <summary>
/// The agent stop that automatic containment engages and its undo lifts (doc 48 section 5.6: the kill-switch actions of doc 15
/// for one agent). It is the kill switch itself, not only the host-side containment: an engaged stop denies every tool effect for
/// the agent and then revokes its leases and pauses its VMs.
/// </summary>
public interface IAgentStop
{
    /// <summary>Engages the agent stop with <paramref name="actorId"/> as the breach actor. Idempotent.</summary>
    Task EngageAsync(string agentId, string actorId, string reason, CancellationToken ct = default);

    /// <summary>
    /// Lifts the agent stop with the owner's signed release. The signature, nonce and device are checked by the kill switch; a
    /// request it does not accept leaves the stop in place and returns <see cref="ContainmentReleaseOutcome.Denied"/>.
    /// </summary>
    Task<ContainmentReleaseResult> ResumeAsync(string agentId, string reason, ContainmentReleaseRequest request, CancellationToken ct = default);
}

/// <summary>
/// The production <see cref="IAgentStop"/>, over <see cref="KillSwitchEngine"/>. Calling <see cref="IAgentContainment"/> directly
/// would revoke leases and pause VMs but leave the agent's stop unset, so <see cref="KillSwitchEngine.Evaluate"/> would keep
/// allowing its tool calls. Going through the engine sets the stop first, records it, and then contains.
/// </summary>
public sealed class KillSwitchAgentStop(KillSwitchEngine engine) : IAgentStop
{
    private readonly KillSwitchEngine _engine = engine ?? throw new ArgumentNullException(nameof(engine));

    /// <inheritdoc />
    public Task EngageAsync(string agentId, string actorId, string reason, CancellationToken ct = default) =>
        _engine.EngageAgentAsync(actorId, agentId, reason, ct);

    /// <inheritdoc />
    public async Task<ContainmentReleaseResult> ResumeAsync(string agentId, string reason, ContainmentReleaseRequest request, CancellationToken ct = default)
    {
        try
        {
            await _engine.ResumeAgentAsync(agentId, reason, request, ct).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException)
        {
            // The engine put the stop back: only the owner's signed request lifts it.
            return new ContainmentReleaseResult(ContainmentReleaseOutcome.Denied);
        }

        return new ContainmentReleaseResult(ContainmentReleaseOutcome.Released);
    }
}
