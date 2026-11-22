using System.Text.Json;
using Bazaar.Server.AccountApproval.Abstractions;
using Bazaar.Server.Security.Audit;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.AccountApproval;

/// <summary>
/// Applies <see cref="SiteAccessPolicy"/> to a finished Council vote: one audit row per decision, and one owner hand-off when the
/// vote does not settle it. It writes no allowlist; the egress and Identity modules apply an <c>OpenReadOnly</c> result.
/// A vote that is not finished yet is returned as <c>Pending</c> and is not audited.
/// </summary>
public sealed class SiteAccessService
{
    private readonly IAuditLog _audit;
    private readonly IOwnerApprovalHandOff _handOff;
    private readonly AccountApprovalOptions _options;
    private readonly TimeProvider _time;

    public SiteAccessService(
        IAuditLog audit, IOwnerApprovalHandOff handOff, IOptions<AccountApprovalOptions> options, TimeProvider? time = null)
    {
        _audit = audit;
        _handOff = handOff;
        _options = options.Value;
        var errors = _options.Validate();
        if (errors.Count > 0)
        {
            throw new OptionsValidationException(nameof(AccountApprovalOptions), typeof(AccountApprovalOptions), errors);
        }

        _time = time ?? TimeProvider.System;
    }

    public async Task<SiteAccessDecision> DecideAsync(SiteTriage triage, SiteVoteResult vote, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(triage);
        ArgumentNullException.ThrowIfNull(vote);
        ArgumentException.ThrowIfNullOrWhiteSpace(triage.Domain);

        var decision = SiteAccessPolicy.Decide(triage, vote, _options);
        if (decision.Outcome == SiteAccessOutcome.Pending)
        {
            return decision;
        }

        await _audit.AppendAsync(
            new AuditEntry(
                _time.GetUtcNow(),
                "site-access",
                "site_access.decision",
                JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["site"] = triage.Domain,
                    ["scope"] = triage.Scope.ToString(),
                    ["outcome"] = decision.Outcome.ToString(),
                    ["rule"] = decision.Rule?.ToString(),
                    ["voteReference"] = vote.VoteReference,
                    ["approve"] = vote.Approve,
                    ["reject"] = vote.Reject,
                    ["abstain"] = vote.Abstain,
                    ["eligible"] = vote.Eligible,
                })),
            ct).ConfigureAwait(false);

        if (decision.Outcome == SiteAccessOutcome.NeedsOwner && decision.Evidence is not null)
        {
            await _handOff.RequestOwnerApprovalAsync(
                new OwnerApprovalRequest($"site:{vote.VoteReference}", decision.Rule!.Value.ToString(), decision.Evidence), ct).ConfigureAwait(false);
        }

        return decision;
    }
}
