using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Bazaar.Server.Approvals;
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
/// completed release reports <see cref="ContainmentReleaseResult.TokensRestored"/> as false.
/// </remarks>
/// <remarks>
/// The contained set is kept in an <see cref="IContainmentStateStore"/>. The adapter writes each contain and release through it
/// and loads the set when it is constructed, so a restarted host still releases the agents it contained. Until the first load
/// succeeds, containment operations wait for it and a failed load is retried by the next call.
/// </remarks>
public sealed class AgentContainmentAdapter : IAgentContainment
{
    /// <summary>How long a release challenge stays valid.</summary>
    public static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(2);

    /// <summary>Audit kind of every release attempt record.</summary>
    public const string ReleaseKind = "containment.release";

    private readonly IVmPauser _pauser;
    private readonly IAgentTokenRevoker _revoker;
    private readonly IOwnerKeyRegistry _owners;
    private readonly IAuditLog _audit;
    private readonly INonceStore _nonces;
    private readonly IContainmentStateStore _store;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, byte> _contained = new(StringComparer.Ordinal);
    private readonly SerialLane _lane = new();
    private readonly Lock _gate = new();
    private bool _loaded; // guarded by _gate
    private Task _loadTask; // guarded by _gate; replaced when a load fails

    /// <summary>Creates an adapter with an in-memory containment store, so the contained set lasts only as long as the process.</summary>
    /// <param name="pauser">Pauses and resumes the agent's VMs.</param>
    /// <param name="revoker">Revokes and (where possible) restores the agent's tokens.</param>
    /// <param name="owners">Owner device registry, which gates release.</param>
    /// <param name="audit">Receives one record per release step.</param>
    /// <param name="nonces">Holds the single-use release nonces.</param>
    /// <param name="timeProvider">Clock for audit and containment instants. Defaults to <see cref="TimeProvider.System"/>.</param>
    public AgentContainmentAdapter(
        IVmPauser pauser,
        IAgentTokenRevoker revoker,
        IOwnerKeyRegistry owners,
        IAuditLog audit,
        INonceStore nonces,
        TimeProvider? timeProvider = null)
        : this(pauser, revoker, owners, audit, nonces, new InMemoryContainmentStateStore(), timeProvider)
    {
    }

    /// <summary>Creates an adapter over a durable containment store and loads the agents it holds.</summary>
    /// <param name="pauser">Pauses and resumes the agent's VMs.</param>
    /// <param name="revoker">Revokes and (where possible) restores the agent's tokens.</param>
    /// <param name="owners">Owner device registry, which gates release.</param>
    /// <param name="audit">Receives one record per release step.</param>
    /// <param name="nonces">Holds the single-use release nonces.</param>
    /// <param name="store">Durable contained-agent set. Every contain and release is written here first.</param>
    /// <param name="timeProvider">Clock for audit and containment instants. Defaults to <see cref="TimeProvider.System"/>.</param>
    public AgentContainmentAdapter(
        IVmPauser pauser,
        IAgentTokenRevoker revoker,
        IOwnerKeyRegistry owners,
        IAuditLog audit,
        INonceStore nonces,
        IContainmentStateStore store,
        TimeProvider? timeProvider = null)
    {
        _pauser = pauser ?? throw new ArgumentNullException(nameof(pauser));
        _revoker = revoker ?? throw new ArgumentNullException(nameof(revoker));
        _owners = owners ?? throw new ArgumentNullException(nameof(owners));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _nonces = nonces ?? throw new ArgumentNullException(nameof(nonces));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _time = timeProvider ?? TimeProvider.System;
        _loadTask = LoadStateAsync(CancellationToken.None);
    }

    /// <inheritdoc />
    public async Task ContainAsync(string agentId, string actorId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        await EnsureLoadedAsync(ct).ConfigureAwait(false);

        // Store and mark first: a contain that fails half way still needs a release afterwards. A failed store write
        // stops the contain before the VMs or tokens are touched.
        await _lane.RunAsync(async () =>
        {
            await _store.AddAsync(new ContainedAgent(agentId, _time.GetUtcNow(), actorId), ct).ConfigureAwait(false);
            _contained[agentId] = 0;
        }, ct).ConfigureAwait(false);

        await _revoker.RevokeAsync(agentId, ct).ConfigureAwait(false);
        await _pauser.PauseAsync(agentId, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ReleaseChallenge?> IssueReleaseChallengeAsync(string agentId, string deviceId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        await EnsureLoadedAsync(ct).ConfigureAwait(false);

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
        await EnsureLoadedAsync(ct).ConfigureAwait(false);

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

        await RemoveContainedAsync(agentId, ct).ConfigureAwait(false);
        return new ContainmentReleaseResult(ContainmentReleaseOutcome.Released, restored);
    }

    private async Task<ContainmentReleaseResult> DenyAsync(string actor, string agentId, string detail, CancellationToken ct)
    {
        await AuditAsync(actor, agentId, "denied", detail, ct).ConfigureAwait(false);
        return new ContainmentReleaseResult(ContainmentReleaseOutcome.Denied);
    }

    /// <summary>Reads the stored contained set into the cache. Runs once from the constructor, and again after a failed load.</summary>
    private async Task LoadStateAsync(CancellationToken ct)
    {
        var agents = await _store.LoadAsync(ct).ConfigureAwait(false);
        lock (_gate)
        {
            _contained.Clear();
            foreach (var agent in agents)
            {
                _contained[agent.AgentId] = 0;
            }
            _loaded = true;
        }
    }

    /// <summary>Waits for the state load. A load that failed is started again, so a transient store outage does not stay fatal.</summary>
    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        Task pending;
        lock (_gate)
        {
            if (_loaded)
            {
                return;
            }
            if (_loadTask.IsFaulted || _loadTask.IsCanceled)
            {
                _loadTask = LoadStateAsync(CancellationToken.None);
            }
            pending = _loadTask;
        }

        await pending.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Removes the agent from the stored set, then from the cache. A failed store write keeps the agent contained.</summary>
    private async Task RemoveContainedAsync(string agentId, CancellationToken ct)
    {
        await _lane.RunAsync(async () =>
        {
            await _store.RemoveAsync(agentId, ct).ConfigureAwait(false);
            _contained.TryRemove(agentId, out _);
        }, ct).ConfigureAwait(false);
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
