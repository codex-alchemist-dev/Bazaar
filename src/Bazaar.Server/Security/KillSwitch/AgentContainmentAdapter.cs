using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Bazaar.Server.Security.Approvals;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Security.Quarantine;

namespace Bazaar.Server.Security.KillSwitch;

/// <summary>
/// Host-side containment for an agent stop (doc 15; kill switch). On contain it revokes the agent's tokens and leases, then
/// pauses its VMs. On release it undoes that in reverse order: VMs first, then tokens.
/// </summary>
/// <remarks>
/// Release is owner-only. The caller passes an owner actor id, which must be a device id that the
/// <see cref="IOwnerKeyRegistry"/> (the approvals code) knows and has not revoked. A failed check resumes nothing. Every
/// attempt writes to <see cref="IAuditLog"/> with kind <see cref="ReleaseKind"/>: one record per step, one record for a
/// denial, one for a release of an agent that was never contained. A VM resume failure is audited and rethrown, and the
/// token resume does not run. Token revocation is not reversible yet (see <see cref="IAgentTokenRevoker.ResumeAsync"/>), so a
/// completed release reports <see cref="ContainmentReleaseResult.TokensRestored"/> as false. Containment is tracked in
/// memory, so a restarted host treats every agent as not contained until a durable store replaces the set.
/// </remarks>
public sealed class AgentContainmentAdapter(
    IVmPauser pauser,
    IAgentTokenRevoker revoker,
    IOwnerKeyRegistry owners,
    IAuditLog audit,
    TimeProvider? timeProvider = null) : IAgentContainment
{
    /// <summary>Audit kind of every release attempt record.</summary>
    public const string ReleaseKind = "containment.release";

    private readonly IVmPauser _pauser = pauser ?? throw new ArgumentNullException(nameof(pauser));
    private readonly IAgentTokenRevoker _revoker = revoker ?? throw new ArgumentNullException(nameof(revoker));
    private readonly IOwnerKeyRegistry _owners = owners ?? throw new ArgumentNullException(nameof(owners));
    private readonly IAuditLog _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, byte> _contained = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public async Task ContainAsync(string agentId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        // Mark first: a contain that fails half way still needs a release afterwards.
        _contained[agentId] = 0;
        await _revoker.RevokeAsync(agentId, ct).ConfigureAwait(false);
        await _pauser.PauseAsync(agentId, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ContainmentReleaseResult> ReleaseAsync(string agentId, string ownerActorId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerActorId);

        var key = await _owners.FindAsync(ownerActorId, ct).ConfigureAwait(false);
        if (key is null || key.Revoked)
        {
            await AuditAsync(ownerActorId, agentId, "denied", "Caller is not a registered owner device.", ct).ConfigureAwait(false);
            return new ContainmentReleaseResult(ContainmentReleaseOutcome.Denied);
        }

        var actor = key.OwnerId;
        if (!_contained.ContainsKey(agentId))
        {
            await AuditAsync(actor, agentId, "not_contained", "Agent was not contained; nothing to resume.", ct).ConfigureAwait(false);
            return new ContainmentReleaseResult(ContainmentReleaseOutcome.NotContained);
        }

        try
        {
            await _pauser.ResumeAsync(agentId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await AuditAsync(actor, agentId, "vm_resume_failed", ex.Message, ct).ConfigureAwait(false);
            throw;
        }

        await AuditAsync(actor, agentId, "vm_resumed", "VMs resumed.", ct).ConfigureAwait(false);

        TokenResumeResult tokens;
        try
        {
            tokens = await _revoker.ResumeAsync(agentId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await AuditAsync(actor, agentId, "token_resume_failed", ex.Message, ct).ConfigureAwait(false);
            throw;
        }

        var restored = tokens.Outcome == TokenResumeOutcome.Restored;
        await AuditAsync(actor, agentId, restored ? "tokens_restored" : "tokens_not_reversible", tokens.Detail, ct).ConfigureAwait(false);

        _contained.TryRemove(agentId, out _);
        return new ContainmentReleaseResult(ContainmentReleaseOutcome.Released, restored);
    }

    private Task AuditAsync(string actor, string agentId, string step, string detail, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["agent_id"] = agentId,
            ["step"] = step,
            ["detail"] = detail,
        };
        return _audit.AppendAsync(new AuditEntry(_time.GetUtcNow(), actor, ReleaseKind, payload.ToJsonString()), ct);
    }
}
