using Bazaar.Server.AccountApproval.Abstractions;

namespace Bazaar.Server.AccountApproval.Tests;

public class AccountApprovalPolicyTests
{
    private static AccountApprovalDecision Decide(
        AccountApprovalRequest request, PeerVote[] votes, ApproverCandidate[]? candidates = null, AccountApprovalOptions? options = null, DateTimeOffset? now = null) =>
        AccountApprovalPolicy.Decide(request, votes, candidates ?? Fixtures.AllGood(), options ?? Fixtures.Options(), now ?? Fixtures.Now);

    [Fact]
    public void Two_of_two_approvals_are_approved_by_peers()
    {
        var d = Decide(Fixtures.Request(), [Fixtures.Vote("agent-a"), Fixtures.Vote("agent-b")]);

        var approved = Assert.IsType<ApprovedByPeers>(d);
        Assert.Equal(DecisionRule.PeerQuorumReached, approved.Rule);
        Assert.Equal(["agent-a", "agent-b"], approved.Approvals.Select(v => v.ApproverAgentId).Order().ToArray());
    }

    [Fact]
    public void One_approval_is_pending_with_one_remaining()
    {
        var pending = Assert.IsType<Pending>(Decide(Fixtures.Request(), [Fixtures.Vote("agent-a")]));

        Assert.Equal(1, pending.Remaining);
        Assert.Equal(2, pending.Quorum);
    }

    [Fact]
    public void No_votes_is_pending_with_full_quorum()
    {
        var pending = Assert.IsType<Pending>(Decide(Fixtures.Request(), []));

        Assert.Equal(2, pending.Remaining);
    }

    [Fact]
    public void Two_of_three_with_one_refusal_goes_to_the_owner()
    {
        var d = Decide(Fixtures.Request(), [Fixtures.Vote("agent-a"), Fixtures.Vote("agent-b"), Fixtures.Vote("agent-c", PeerVoteKind.Refuse)]);

        var owner = Assert.IsType<NeedsOwner>(d);
        Assert.Equal(DecisionRule.PeerRefused, owner.Rule);
        Assert.Equal(3, owner.Evidence.PeerVotes.Count);
    }

    [Fact]
    public void A_single_refusal_goes_to_the_owner()
    {
        var d = Decide(Fixtures.Request(), [Fixtures.Vote("agent-a", PeerVoteKind.Refuse)]);

        Assert.Equal(DecisionRule.PeerRefused, Assert.IsType<NeedsOwner>(d).Rule);
    }

    [Fact]
    public void An_approver_who_approved_and_then_refused_counts_as_a_refusal()
    {
        var d = Decide(Fixtures.Request(), [Fixtures.Vote("agent-a"), Fixtures.Vote("agent-a", PeerVoteKind.Refuse), Fixtures.Vote("agent-b")]);

        Assert.Equal(DecisionRule.PeerRefused, Assert.IsType<NeedsOwner>(d).Rule);
    }

    [Fact]
    public void The_same_approver_twice_counts_once()
    {
        var d = Decide(Fixtures.Request(), [Fixtures.Vote("agent-a"), Fixtures.Vote("agent-a")]);

        Assert.Equal(1, Assert.IsType<Pending>(d).Remaining);
    }

    [Fact]
    public void Requester_self_approval_is_ignored_and_the_requester_is_not_eligible()
    {
        var request = Fixtures.Request(requester: "agent-a");

        var d = Decide(request, [Fixtures.Vote("agent-a"), Fixtures.Vote("agent-b")]);

        Assert.Equal(1, Assert.IsType<Pending>(d).Remaining);
        Assert.DoesNotContain("agent-a", AccountApprovalPolicy.EligibleApprovers(request, Fixtures.AllGood(), Fixtures.Options()));
    }

    [Fact]
    public void A_refusal_from_the_requester_is_ignored()
    {
        var d = Decide(Fixtures.Request(requester: "agent-a"), [Fixtures.Vote("agent-a", PeerVoteKind.Refuse)]);

        Assert.IsType<Pending>(d);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void An_approver_who_is_tainted_non_frontier_or_asleep_is_not_eligible(bool untainted, bool frontier, bool awake)
    {
        ApproverCandidate[] candidates =
        [
            new("agent-a", new ApproverStanding(untainted, frontier, awake)),
            new("agent-b", Fixtures.Good),
            new("agent-c", Fixtures.Good),
        ];

        var d = Decide(Fixtures.Request(), [Fixtures.Vote("agent-a"), Fixtures.Vote("agent-b")], candidates);

        Assert.Equal(1, Assert.IsType<Pending>(d).Remaining);
    }

    [Fact]
    public void A_tainted_approver_cannot_refuse_either()
    {
        ApproverCandidate[] candidates = [new("agent-a", new ApproverStanding(false, true, true)), new("agent-b", Fixtures.Good), new("agent-c", Fixtures.Good)];

        var d = Decide(Fixtures.Request(), [Fixtures.Vote("agent-a", PeerVoteKind.Refuse)], candidates);

        Assert.IsType<Pending>(d);
    }

    [Fact]
    public void An_approver_not_on_the_list_is_ignored()
    {
        var d = Decide(Fixtures.Request(), [Fixtures.Vote("agent-x"), Fixtures.Vote("agent-a")], Fixtures.AllGood("agent-a", "agent-b", "agent-c", "agent-x"));

        Assert.Equal(1, Assert.IsType<Pending>(d).Remaining);
    }

    [Fact]
    public void Too_few_eligible_peers_goes_to_the_owner()
    {
        ApproverCandidate[] candidates = [new("agent-a", Fixtures.Good), new("agent-b", ApproverStanding.None), new("agent-c", ApproverStanding.None)];

        var d = Decide(Fixtures.Request(), [Fixtures.Vote("agent-a")], candidates);

        Assert.Equal(DecisionRule.TooFewEligiblePeers, Assert.IsType<NeedsOwner>(d).Rule);
    }

    [Fact]
    public void No_candidates_at_all_goes_to_the_owner()
    {
        var d = Decide(Fixtures.Request(), [], []);

        Assert.Equal(DecisionRule.TooFewEligiblePeers, Assert.IsType<NeedsOwner>(d).Rule);
    }

    [Fact]
    public void A_higher_quorum_is_honoured()
    {
        var options = Fixtures.Options(quorum: 3);

        Assert.IsType<Pending>(Decide(Fixtures.Request(), [Fixtures.Vote("agent-a"), Fixtures.Vote("agent-b")], options: options));
        Assert.IsType<ApprovedByPeers>(Decide(Fixtures.Request(), [Fixtures.Vote("agent-a"), Fixtures.Vote("agent-b"), Fixtures.Vote("agent-c")], options: options));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(-3)]
    public void A_quorum_below_two_never_approves(int quorum)
    {
        var d = Decide(Fixtures.Request(), [Fixtures.Vote("agent-a")], options: Fixtures.Options(quorum: quorum));

        Assert.Equal(DecisionRule.InvalidQuorum, Assert.IsType<NeedsOwner>(d).Rule);
    }

    [Theory]
    [InlineData(ServiceTermsStatus.Unverified, DecisionRule.UnverifiedTerms)]
    [InlineData(ServiceTermsStatus.ForbidsAutomation, DecisionRule.TermsForbidAutomation)]
    public void Terms_that_are_unverified_or_forbid_automation_are_denied_whatever_the_votes(ServiceTermsStatus terms, DecisionRule rule)
    {
        var d = Decide(Fixtures.Request(terms: terms), [Fixtures.Vote("agent-a"), Fixtures.Vote("agent-b")]);

        Assert.Equal(rule, Assert.IsType<Denied>(d).Rule);
    }

    [Fact]
    public void An_account_in_another_persons_name_is_denied()
    {
        var d = Decide(Fixtures.Request(otherPerson: true), [Fixtures.Vote("agent-a"), Fixtures.Vote("agent-b")]);

        Assert.Equal(DecisionRule.AnotherPersonsName, Assert.IsType<Denied>(d).Rule);
    }

    [Fact]
    public void Deny_wins_over_spend()
    {
        var d = Decide(Fixtures.Request(terms: ServiceTermsStatus.Unverified, spend: true), []);

        Assert.IsType<Denied>(d);
    }

    [Fact]
    public void Spend_goes_to_the_owner_even_with_a_full_quorum()
    {
        var d = Decide(Fixtures.Request(spend: true), [Fixtures.Vote("agent-a"), Fixtures.Vote("agent-b")]);

        Assert.Equal(DecisionRule.Spend, Assert.IsType<NeedsOwner>(d).Rule);
    }

    [Fact]
    public void Another_tier_2_action_goes_to_the_owner_even_with_a_full_quorum()
    {
        var d = Decide(Fixtures.Request(action: RequestedAction.OtherTier2), [Fixtures.Vote("agent-a"), Fixtures.Vote("agent-b")]);

        Assert.Equal(DecisionRule.OtherTier2Action, Assert.IsType<NeedsOwner>(d).Rule);
    }

    [Fact]
    public void A_service_off_the_signup_whitelist_goes_to_the_owner()
    {
        var d = Decide(Fixtures.Request(whitelisted: false), [Fixtures.Vote("agent-a"), Fixtures.Vote("agent-b")]);

        Assert.Equal(DecisionRule.NotOnSignupWhitelist, Assert.IsType<NeedsOwner>(d).Rule);
    }

    [Fact]
    public void An_expired_request_goes_to_the_owner_at_the_boundary()
    {
        var votes = new[] { Fixtures.Vote("agent-a"), Fixtures.Vote("agent-b") };
        var expiry = Fixtures.Now.AddHours(24);

        Assert.IsType<ApprovedByPeers>(Decide(Fixtures.Request(), votes, now: expiry.AddTicks(-1)));
        Assert.Equal(DecisionRule.ApprovalExpired, Assert.IsType<NeedsOwner>(Decide(Fixtures.Request(), votes, now: expiry)).Rule);
    }

    [Fact]
    public void Approval_carries_the_expiry()
    {
        var approved = Assert.IsType<ApprovedByPeers>(Decide(Fixtures.Request(), [Fixtures.Vote("agent-a"), Fixtures.Vote("agent-b")]));

        Assert.Equal(Fixtures.Now.AddHours(24), approved.ExpiresAt);
    }

    [Fact]
    public void By_default_a_person_accepts_the_terms()
    {
        var approved = Assert.IsType<ApprovedByPeers>(Decide(Fixtures.Request(), [Fixtures.Vote("agent-a"), Fixtures.Vote("agent-b")]));

        Assert.False(approved.AgentMayAcceptTerms);
        Assert.True(approved.PersonSteps.HasFlag(PersonStep.LegalTerms));
        Assert.Null(approved.TermsToAccept);
    }

    [Fact]
    public void With_the_setting_on_the_agent_accepts_the_terms_and_the_terms_are_recorded()
    {
        var options = Fixtures.Options(agentAcceptsTerms: true);

        var approved = Assert.IsType<ApprovedByPeers>(Decide(Fixtures.Request(), [Fixtures.Vote("agent-a"), Fixtures.Vote("agent-b")], options: options));

        Assert.True(approved.AgentMayAcceptTerms);
        Assert.False(approved.PersonSteps.HasFlag(PersonStep.LegalTerms));
        Assert.Equal("https://example.test/terms", approved.TermsToAccept);
        Assert.Equal("v1", approved.TermsVersionOrHash);
    }

    [Theory]
    [InlineData(PersonStep.Captcha)]
    [InlineData(PersonStep.Phone)]
    [InlineData(PersonStep.IdCheck)]
    [InlineData(PersonStep.Passkey)]
    [InlineData(PersonStep.RecoveryCode)]
    public void Captcha_phone_id_passkey_and_recovery_steps_always_need_a_person(PersonStep step)
    {
        var options = Fixtures.Options(agentAcceptsTerms: true);

        var approved = Assert.IsType<ApprovedByPeers>(Decide(Fixtures.Request(steps: step), [Fixtures.Vote("agent-a"), Fixtures.Vote("agent-b")], options: options));

        Assert.True(approved.PersonSteps.HasFlag(step));
    }

    [Fact]
    public void Owner_hand_off_attaches_the_site_vote_and_terms_link()
    {
        var vote = new SiteVoteResult(SiteVoteResult.SiteAllowlistMotion, "example.test", "vote-9", true, 3, 2, 1, 0);
        var request = Fixtures.Request(spend: true) with { SiteVote = vote };

        var owner = Assert.IsType<NeedsOwner>(Decide(request, []));

        Assert.Equal(vote, owner.Evidence.SiteVote);
        Assert.Equal("https://example.test/terms", owner.Evidence.TermsUrl);
        Assert.Equal("agent-r", owner.Evidence.RequesterAgentId);
    }
}
