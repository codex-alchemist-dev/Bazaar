using Bazaar.Server.AccountApproval.Abstractions;

namespace Bazaar.Server.AccountApproval;

/// <summary>
/// The pure outcome of a finished Council vote on motion <c>site_allowlist</c> (doc 59 section 7, DECISIONS-LOG round 28).
/// A unanimous vote opens a site for login-free reading only. Anything else, and every account or logged-in request, goes to
/// the owner with the vote attached. Unverified terms and terms that forbid automation stay Deny whatever the vote says.
/// </summary>
public static class SiteAccessPolicy
{
    public static SiteAccessDecision Decide(SiteTriage triage, SiteVoteResult vote, AccountApprovalOptions options)
    {
        var domain = triage.Domain;
        var evidence = new ApprovalEvidence(domain, string.Empty, triage.TermsUrl, vote, []);

        if (triage.Terms == ServiceTermsStatus.Unverified)
        {
            return new SiteAccessDecision(domain, SiteAccessOutcome.Denied, DecisionRule.UnverifiedTerms, vote.VoteReference, null);
        }

        if (triage.Terms == ServiceTermsStatus.ForbidsAutomation)
        {
            return new SiteAccessDecision(domain, SiteAccessOutcome.Denied, DecisionRule.TermsForbidAutomation, vote.VoteReference, null);
        }

        // A vote on another motion or another site proves nothing here.
        if (!string.Equals(vote.MotionKind, SiteVoteResult.SiteAllowlistMotion, StringComparison.Ordinal)
            || !string.Equals(vote.Subject, domain, StringComparison.OrdinalIgnoreCase))
        {
            return Owner(domain, DecisionRule.VoteDoesNotMatchSite, vote, evidence);
        }

        if (!vote.Finished)
        {
            return new SiteAccessDecision(domain, SiteAccessOutcome.Pending, DecisionRule.VoteNotFinished, vote.VoteReference, null);
        }

        // Every account or logged-in use is a separate Tier 2 approval, even after a unanimous vote.
        if (triage.Scope != SiteScope.ReadOnly)
        {
            return Owner(domain, DecisionRule.AccountScopeIsTier2, vote, evidence);
        }

        var cast = vote.Approve + vote.Reject + vote.Abstain;
        var unanimousApproval = vote.Eligible > 0
            && cast == vote.Eligible
            && vote.Approve == vote.Eligible
            && vote.Reject == 0
            && vote.Abstain == 0;

        if (unanimousApproval && vote.Eligible >= options.MinUnanimousVoters)
        {
            return new SiteAccessDecision(domain, SiteAccessOutcome.OpenReadOnly, DecisionRule.UnanimousReadOnlyVote, vote.VoteReference, evidence);
        }

        if (unanimousApproval)
        {
            return Owner(domain, DecisionRule.VoteTooSmall, vote, evidence);
        }

        var allRejected = vote.Eligible > 0 && vote.Reject == vote.Eligible && cast == vote.Eligible;
        return Owner(domain, allRejected ? DecisionRule.PeersRejectedSite : DecisionRule.SplitVote, vote, evidence);
    }

    private static SiteAccessDecision Owner(string domain, DecisionRule rule, SiteVoteResult vote, ApprovalEvidence evidence) =>
        new(domain, SiteAccessOutcome.NeedsOwner, rule, vote.VoteReference, evidence);
}
