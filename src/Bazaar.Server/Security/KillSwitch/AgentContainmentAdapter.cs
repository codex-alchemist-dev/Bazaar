using Bazaar.Server.Security.Quarantine;

namespace Bazaar.Server.Security.KillSwitch;

/// <summary>
/// Host-side containment for an agent stop (doc 15; kill switch). On contain it revokes the agent's tokens and leases, then
/// pauses its VMs. The order matches <see cref="QuarantineService"/>, so access stops before the VMs pause.
/// </summary>
/// <remarks>
/// <see cref="IVmPauser"/> and <see cref="IAgentTokenRevoker"/> have no resume or restore call, so <see cref="ReleaseAsync"/>
/// has nothing to undo yet. It returns without calling either interface. Leases stay revoked, as
/// <see cref="IAgentContainment.ReleaseAsync"/> documents. Both contain calls are idempotent by contract, so a retried
/// contain repeats them safely.
/// </remarks>
public sealed class AgentContainmentAdapter(IVmPauser pauser, IAgentTokenRevoker revoker) : IAgentContainment
{
    private readonly IVmPauser _pauser = pauser ?? throw new ArgumentNullException(nameof(pauser));
    private readonly IAgentTokenRevoker _revoker = revoker ?? throw new ArgumentNullException(nameof(revoker));

    /// <inheritdoc />
    public async Task ContainAsync(string agentId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        await _revoker.RevokeAsync(agentId, ct).ConfigureAwait(false);
        await _pauser.PauseAsync(agentId, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task ReleaseAsync(string agentId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        // Nothing to undo: no unpause or token restore exists in the host contracts yet (see the remarks).
        return Task.CompletedTask;
    }
}
