using Bazaar.Server.AccountApproval.Abstractions;

namespace Bazaar.Server.AccountApproval;

/// <summary>
/// The pure rules of doc 15 "Peer approval of account creation". No I/O, no clock, no state: the caller passes the roster
/// standings and the votes so far. Anything not clearly allowed falls to the owner; the Deny cases never do.
/// </summary>
public static class AccountApprovalPolicy
{
    private const PersonStep _alwaysPerson = PersonStep.Captcha | PersonStep.Phone | PersonStep.IdCheck | PersonStep.Passkey | PersonStep.RecoveryCode;

    /// <summary>The agents that may approve this request now: listed, not the requester, untainted, frontier and awake.</summary>
    public static IReadOnlyList<string> EligibleApprovers(
        AccountApprovalRequest request, IReadOnlyList<ApproverCandidate> candidates, AccountApprovalOptions options)
    {
        var listed = options.PeerApprovers.ToHashSet(StringComparer.Ordinal);
        return candidates
            .Where(c => listed.Contains(c.AgentId)
                && !string.Equals(c.AgentId, request.RequesterAgentId, StringComparison.Ordinal)
                && c.Standing.SessionUntainted
                && c.Standing.RunsFrontierModel
                && c.Standing.Awake)
            .Select(c => c.AgentId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public static AccountApprovalDecision Decide(
        AccountApprovalRequest request,
        IReadOnlyList<PeerVote> votes,
        IReadOnlyList<ApproverCandidate> candidates,
        AccountApprovalOptions options,
        DateTimeOffset now)
    {
        var id = request.RequestId;

        // Deny first: no vote and no owner step changes these (doc 15 Deny row).
        if (request.Terms == ServiceTermsStatus.Unverified)
        {
            return new Denied(id, DecisionRule.UnverifiedTerms);
        }

        if (request.Terms == ServiceTermsStatus.ForbidsAutomation)
        {
            return new Denied(id, DecisionRule.TermsForbidAutomation);
        }

        if (request.InAnotherPersonsName)
        {
            return new Denied(id, DecisionRule.AnotherPersonsName);
        }

        // Peers cannot approve these, so they go to the owner.
        if (request.Action != RequestedAction.CreateAccount)
        {
            return Owner(request, votes, DecisionRule.OtherTier2Action);
        }

        if (request.InvolvesSpend)
        {
            return Owner(request, votes, DecisionRule.Spend);
        }

        if (!request.ServiceOnSignupWhitelist)
        {
            return Owner(request, votes, DecisionRule.NotOnSignupWhitelist);
        }

        if (options.PeerQuorum < AccountApprovalOptions.MinimumQuorum)
        {
            return Owner(request, votes, DecisionRule.InvalidQuorum);
        }

        var expiresAt = request.CreatedAt + TimeSpan.FromHours(options.ApprovalExpiryHours);
        if (now >= expiresAt)
        {
            return Owner(request, votes, DecisionRule.ApprovalExpired);
        }

        var eligible = EligibleApprovers(request, candidates, options).ToHashSet(StringComparer.Ordinal);
        var counted = votes
            .Where(v => eligible.Contains(v.ApproverAgentId))
            .GroupBy(v => v.ApproverAgentId, StringComparer.Ordinal)
            .Select(g => g.Any(v => v.Kind == PeerVoteKind.Refuse)
                ? g.First(v => v.Kind == PeerVoteKind.Refuse)
                : g.OrderBy(v => v.CastAt).First())
            .ToArray();

        // One refusal sends it to the owner; peers cannot overrule each other.
        if (counted.Any(v => v.Kind == PeerVoteKind.Refuse))
        {
            return Owner(request, votes, DecisionRule.PeerRefused);
        }

        if (eligible.Count < options.PeerQuorum)
        {
            return Owner(request, votes, DecisionRule.TooFewEligiblePeers);
        }

        if (counted.Length < options.PeerQuorum)
        {
            return new Pending(id, options.PeerQuorum - counted.Length, options.PeerQuorum);
        }

        var steps = request.PersonSteps & _alwaysPerson;
        if (!options.AgentAcceptsTerms)
        {
            steps |= PersonStep.LegalTerms;
        }

        return new ApprovedByPeers(
            id,
            DecisionRule.PeerQuorumReached,
            counted.OrderBy(v => v.CastAt).ToArray(),
            steps,
            options.AgentAcceptsTerms,
            options.AgentAcceptsTerms ? request.TermsUrl : null,
            options.AgentAcceptsTerms ? request.TermsVersionOrHash : null,
            expiresAt);
    }

    private static NeedsOwner Owner(AccountApprovalRequest request, IReadOnlyList<PeerVote> votes, DecisionRule rule) =>
        new(request.RequestId, rule, new ApprovalEvidence(request.Service, request.RequesterAgentId, request.TermsUrl, request.SiteVote, votes));
}
