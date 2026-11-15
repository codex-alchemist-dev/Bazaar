using System.Collections.Concurrent;
using System.Security.Cryptography;
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
/// Release is owner-only and signed. The caller first gets a single-use nonce from <see cref="IssueReleaseChallengeAsync"/>,
/// then sends a <see cref="ContainmentReleaseRequest"/> signed by an owner device key that the
/// <see cref="IOwnerKeyRegistry"/> knows and has not revoked. The signature uses the approvals scheme
/// (<see cref="ApprovalSignature.ReleaseMessage"/>, <see cref="ApprovalSignature.Verify"/>). A nonce is consumed by the first
/// request whose signature verifies, whatever the outcome; an unverifiable request does not consume it. A failed check resumes nothing. Every
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
    INonceStore nonces,
    TimeProvider? timeProvider = null) : IAgentContainment
{
    /// <summary>How long a release challenge stays valid.</summary>
    public static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(2);

    /// <summary>Audit kind of every release attempt record.</summary>
    public const string ReleaseKind = "containment.release";

    private readonly IVmPauser _pauser = pauser ?? throw new ArgumentNullException(nameof(pauser));
    private readonly IAgentTokenRevoker _revoker = revoker ?? throw new ArgumentNullException(nameof(revoker));
    private readonly IOwnerKeyRegistry _owners = owners ?? throw new ArgumentNullException(nameof(owners));
    private readonly INonceStore _nonces = nonces ?? throw new ArgumentNullException(nameof(nonces));
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
    public async Task<ReleaseChallenge?> IssueReleaseChallengeAsync(string agentId, string deviceId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        var key = ApprovalSignature.IsValidDeviceId(deviceId) ? await _owners.FindAsync(deviceId, ct).ConfigureAwait(false) : null;
        if (key is null || key.Revoked)
        {
            await AuditAsync(deviceId, agentId, "challenge_denied", "Caller is not a registered owner device.", ct).ConfigureAwait(false);
            return null;
        }

        var nonce = RandomNumberGenerator.GetBytes(ApprovalSignature.ReleaseNonceLength);
        var expiresAt = _time.GetUtcNow().Add(ChallengeLifetime);
        // Whole milliseconds, because that is what the signed message carries.
        expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(expiresAt.ToUnixTimeMilliseconds());
        await _nonces.AddAsync(new PendingNonce(nonce, agentId, deviceId, expiresAt), ct).ConfigureAwait(false);
        await AuditAsync(key.OwnerId, agentId, "challenge_issued", $"Release challenge issued to {deviceId}.", ct).ConfigureAwait(false);
        return new ReleaseChallenge(nonce, expiresAt);
    }

    /// <inheritdoc />
    public async Task<ContainmentReleaseResult> ReleaseAsync(string agentId, ContainmentReleaseRequest request, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(request);
        var claimed = request.DeviceId ?? string.Empty;

        // The agent id in the request is what was signed, so it must be the agent being released.
        if (!string.Equals(request.AgentId, agentId, StringComparison.Ordinal))
        {
            return await DenyAsync(claimed, agentId, "Request is for a different agent.", ct).ConfigureAwait(false);
        }

        var key = ApprovalSignature.IsValidDeviceId(claimed) ? await _owners.FindAsync(claimed, ct).ConfigureAwait(false) : null;
        if (key is null || key.Revoked)
        {
            return await DenyAsync(claimed, agentId, "Caller is not a registered owner device.", ct).ConfigureAwait(false);
        }

        if (request.Nonce is not { Length: ApprovalSignature.ReleaseNonceLength } || request.Signature is null)
        {
            return await DenyAsync(claimed, agentId, "Request is malformed.", ct).ConfigureAwait(false);
        }

        var message = ApprovalSignature.ReleaseMessage(agentId, claimed, request.Nonce, request.ExpiresAt);
        if (!ApprovalSignature.Verify(key.PublicKeySpki, message, request.Signature))
        {
            // Not consumed: a request nobody holding the key signed must not be able to burn the owner's nonce.
            return await DenyAsync(claimed, agentId, "Signature does not verify.", ct).ConfigureAwait(false);
        }

        // From here the request is authentic. The nonce is consumed whatever happens next.
        var pending = await _nonces.TryConsumeAsync(request.Nonce, ct).ConfigureAwait(false);
        if (pending is null)
        {
            return await DenyAsync(claimed, agentId, "Nonce was not issued or was already used.", ct).ConfigureAwait(false);
        }

        if (!string.Equals(pending.AgentId, agentId, StringComparison.Ordinal) || !string.Equals(pending.DeviceId, claimed, StringComparison.Ordinal))
        {
            return await DenyAsync(claimed, agentId, "Nonce was issued for a different agent or device.", ct).ConfigureAwait(false);
        }

        if (pending.ExpiresAt <= _time.GetUtcNow() || request.ExpiresAt != pending.ExpiresAt)
        {
            return await DenyAsync(claimed, agentId, "Nonce has expired or the signed expiry does not match.", ct).ConfigureAwait(false);
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

    private async Task<ContainmentReleaseResult> DenyAsync(string actor, string agentId, string detail, CancellationToken ct)
    {
        await AuditAsync(actor, agentId, "denied", detail, ct).ConfigureAwait(false);
        return new ContainmentReleaseResult(ContainmentReleaseOutcome.Denied);
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
