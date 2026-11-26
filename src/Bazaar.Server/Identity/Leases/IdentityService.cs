using System.Text.Json.Nodes;
using Bazaar.Server.Identity;
using Bazaar.Server.Security.Policy;
using Bazaar.Server.Security.Taint;

namespace Bazaar.Server.Identity.Leases;

/// <summary>
/// Implements <see cref="IIdentityService"/> (doc 14, "Access flow"). Order of checks: quarantine, account, taint and
/// tier, approval, then audit and issue. Audit is written before any lease is issued; a failed audit write issues nothing.
/// </summary>
public sealed class IdentityService : IIdentityService
{
    private readonly IdentityOptions _options;
    private readonly IAgentStateReader _agents;
    private readonly ICredentialDirectory _credentials;
    private readonly ICredentialLeaseStore _leases;
    private readonly IIdentityAudit _audit;
    private readonly IAccessApprovalSource _approvals;
    private readonly TimeProvider _time;
    private readonly Func<AccessRequest, ToolTier> _tierFor;

    /// <summary>
    /// Creates the service. <paramref name="tierFor"/> maps a request to its tier; when null, the default mapping applies
    /// (<see cref="DefaultTierFor"/>).
    /// </summary>
    public IdentityService(
        IdentityOptions options,
        IAgentStateReader agents,
        ICredentialDirectory credentials,
        ICredentialLeaseStore leases,
        IIdentityAudit audit,
        IAccessApprovalSource approvals,
        TimeProvider time,
        Func<AccessRequest, ToolTier>? tierFor = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.LeaseTtlMinutes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "identity.lease_ttl_minutes must be positive.");
        }

        _options = options;
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _leases = leases ?? throw new ArgumentNullException(nameof(leases));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _approvals = approvals ?? throw new ArgumentNullException(nameof(approvals));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _tierFor = tierFor ?? DefaultTierFor;
    }

    /// <summary>
    /// Default tier mapping used when no mapping is supplied: Tier 2 for actions whose name contains "write" or "spend"
    /// (case-insensitive), otherwise Tier 1. Replace it with the policy table when the caller has one.
    /// </summary>
    public static ToolTier DefaultTierFor(AccessRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var action = request.Action;
        var owner = action.Contains("write", StringComparison.OrdinalIgnoreCase)
            || action.Contains("spend", StringComparison.OrdinalIgnoreCase);
        return owner ? ToolTier.OwnerApproves : ToolTier.AutonomousWithNotice;
    }

    /// <inheritdoc />
    public async Task<AccessDecision> RequestAccessAsync(AccessRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.AgentId)
            || string.IsNullOrWhiteSpace(request.Service)
            || string.IsNullOrWhiteSpace(request.Action))
        {
            return new Denied("Malformed access request.");
        }

        // 1. Quarantined agents cannot request leases; their existing leases are revoked (doc 14, "Replay, restart, quarantine").
        if (_agents.IsQuarantined(request.AgentId))
        {
            await _leases.RevokeAgentAsync(request.AgentId, ct).ConfigureAwait(false);
            return new Denied("Agent is quarantined.");
        }

        // 2. Account check: no Passport entry for this agent and service means a person must create it.
        var credentialId = await _credentials.FindCredentialAsync(request.AgentId, request.Service, ct).ConfigureAwait(false);
        if (credentialId is not { } credential)
        {
            return new HumanRequired("No account for this agent and service; a person must create it.");
        }

        // 3 and 4. Taint and tier. Hard taint escalates Tier 1 to Tier 2, as in ToolPolicyEngine (doc 15).
        var hard = _agents.TaintFor(request.AgentId).State == TaintState.Hard;
        var tier = _tierFor(request);
        var effective = tier == ToolTier.AutonomousWithNotice && hard ? ToolTier.OwnerApproves : tier;

        Guid? approvalId = null;
        switch (effective)
        {
            case ToolTier.Denied:
                return new Denied("Policy denies this action.");

            case ToolTier.HumanPerforms:
                return new HumanRequired("A person must perform this action.");

            case ToolTier.OwnerApproves when hard:
                return new Denied("Hard taint blocks Tier 2 leases; use a fresh session with an approved summary.");

            case ToolTier.OwnerApproves:
                // 5. Tier 2 waits for a per-action owner approval, which is single use.
                var approved = await _approvals.FindApprovedAsync(request, ct).ConfigureAwait(false);
                if (approved is not { } approvedId)
                {
                    return new NeedsApproval(Guid.CreateVersion7(_time.GetUtcNow()), effective);
                }

                if (await _leases.IsApprovalUsedAsync(approvedId, ct).ConfigureAwait(false))
                {
                    return new Denied("This approval has already been used.");
                }

                approvalId = approvedId;
                break;
        }

        // 6. Audit first. A failed append (not a cancellation) issues nothing.
        var now = _time.GetUtcNow();
        var leaseId = new LeaseId(Guid.CreateVersion7(now));
        var expiresAt = now + TimeSpan.FromMinutes(_options.LeaseTtlMinutes);
        var scope = $"{request.Service}:{request.Action}";
        var payload = new JsonObject
        {
            ["agent"] = request.AgentId,
            ["service"] = request.Service,
            ["action"] = request.Action,
            ["purpose"] = request.Purpose,
            ["lease"] = leaseId.Value.ToString(),
            ["credential"] = credential.Value.ToString(),
            ["tier"] = effective.ToString(),
            ["approval"] = approvalId?.ToString(),
            ["expires"] = expiresAt.ToString("O"),
        }.ToJsonString();

        try
        {
            await _audit.AppendAsync("lease.issued", payload, ct).ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return new Denied("Audit write failed; no lease was issued.");
        }

        // 7. Issue. The store refuses an approval id that another request consumed in the meantime.
        var lease = await _leases.IssueAsync(leaseId, request.AgentId, credential, scope, expiresAt, approvalId, ct)
            .ConfigureAwait(false);
        if (lease is null)
        {
            return new Denied("This approval has already been used.");
        }

        return new Granted(lease.Id, lease.ExpiresAt);
    }

    /// <inheritdoc />
    public async Task RevokeAsync(RevocationScope scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await _leases.RevokeAsync(scope, ct).ConfigureAwait(false);

        var payload = scope switch
        {
            AgentRevocation agent => new JsonObject { ["scope"] = "agent", ["agent"] = agent.AgentId }.ToJsonString(),
            GlobalRevocation => new JsonObject { ["scope"] = "global" }.ToJsonString(),
            _ => throw new ArgumentException("Unknown revocation scope.", nameof(scope)),
        };

        await _audit.AppendAsync("lease.revoked", payload, ct).ConfigureAwait(false);
    }
}
