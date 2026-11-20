using System.Text.Json;
using Bazaar.Server.AccountApproval.Abstractions;

namespace Bazaar.Server.AccountApproval.Tests;

public class SiteAccessPolicyTests
{
    private static SiteVoteResult Vote(int eligible, int approve, int reject, int abstain, bool finished = true, string motion = SiteVoteResult.SiteAllowlistMotion, string subject = "example.test") =>
        new(motion, subject, "vote-1", finished, eligible, approve, reject, abstain);

    private static SiteTriage Triage(ServiceTermsStatus terms = ServiceTermsStatus.Verified, SiteScope scope = SiteScope.ReadOnly) =>
        new("example.test", terms, scope, "https://example.test/terms");

    private static SiteAccessDecision Decide(SiteTriage t, SiteVoteResult v, AccountApprovalOptions? o = null) => SiteAccessPolicy.Decide(t, v, o ?? new AccountApprovalOptions());

    [Fact]
    public void A_unanimous_vote_opens_reading_with_no_owner_step()
    {
        var d = Decide(Triage(), Vote(3, 3, 0, 0));

        Assert.Equal(SiteAccessOutcome.OpenReadOnly, d.Outcome);
        Assert.Equal(DecisionRule.UnanimousReadOnlyVote, d.Rule);
        Assert.Equal("vote-1", d.VoteReference);
    }

    [Theory]
    [InlineData(3, 2, 1, 0)]
    [InlineData(4, 2, 2, 0)]
    [InlineData(3, 2, 0, 1)]
    [InlineData(3, 2, 0, 0)]
    public void A_split_tied_or_short_vote_goes_to_the_owner_with_the_vote_attached(int eligible, int approve, int reject, int abstain)
    {
        var d = Decide(Triage(), Vote(eligible, approve, reject, abstain));

        Assert.Equal(SiteAccessOutcome.NeedsOwner, d.Outcome);
        Assert.Equal(DecisionRule.SplitVote, d.Rule);
        Assert.Equal("vote-1", d.Evidence!.SiteVote!.VoteReference);
    }

    [Fact]
    public void A_unanimous_rejection_goes_to_the_owner_and_opens_nothing()
    {
        var d = Decide(Triage(), Vote(3, 0, 3, 0));

        Assert.Equal(SiteAccessOutcome.NeedsOwner, d.Outcome);
        Assert.Equal(DecisionRule.PeersRejectedSite, d.Rule);
    }

    [Fact]
    public void A_unanimous_vote_of_too_few_voters_goes_to_the_owner()
    {
        var d = Decide(Triage(), Vote(1, 1, 0, 0));

        Assert.Equal(DecisionRule.VoteTooSmall, d.Rule);
        Assert.Equal(SiteAccessOutcome.NeedsOwner, d.Outcome);
    }

    [Fact]
    public void The_minimum_voters_setting_is_honoured()
    {
        var d = Decide(Triage(), Vote(2, 2, 0, 0), new AccountApprovalOptions { MinUnanimousVoters = 3 });

        Assert.Equal(DecisionRule.VoteTooSmall, d.Rule);
    }

    [Fact]
    public void A_vote_with_nobody_eligible_never_opens_a_site()
    {
        var d = Decide(Triage(), Vote(0, 0, 0, 0), new AccountApprovalOptions { MinUnanimousVoters = 1 });

        Assert.Equal(SiteAccessOutcome.NeedsOwner, d.Outcome);
    }

    [Fact]
    public void An_unfinished_vote_is_pending()
    {
        var d = Decide(Triage(), Vote(3, 3, 0, 0, finished: false));

        Assert.Equal(SiteAccessOutcome.Pending, d.Outcome);
    }

    [Theory]
    [InlineData(ServiceTermsStatus.Unverified, DecisionRule.UnverifiedTerms)]
    [InlineData(ServiceTermsStatus.ForbidsAutomation, DecisionRule.TermsForbidAutomation)]
    public void Unverified_or_automation_forbidding_terms_stay_denied_after_a_unanimous_vote(ServiceTermsStatus terms, DecisionRule rule)
    {
        var d = Decide(Triage(terms), Vote(3, 3, 0, 0));

        Assert.Equal(SiteAccessOutcome.Denied, d.Outcome);
        Assert.Equal(rule, d.Rule);
    }

    [Fact]
    public void An_account_request_goes_to_the_owner_even_after_a_unanimous_vote()
    {
        var d = Decide(Triage(scope: SiteScope.Account), Vote(3, 3, 0, 0));

        Assert.Equal(SiteAccessOutcome.NeedsOwner, d.Outcome);
        Assert.Equal(DecisionRule.AccountScopeIsTier2, d.Rule);
        Assert.NotNull(d.Evidence!.SiteVote);
    }

    [Fact]
    public void A_vote_on_another_motion_or_another_site_proves_nothing()
    {
        Assert.Equal(DecisionRule.VoteDoesNotMatchSite, Decide(Triage(), Vote(3, 3, 0, 0, motion: "other_motion")).Rule);
        Assert.Equal(DecisionRule.VoteDoesNotMatchSite, Decide(Triage(), Vote(3, 3, 0, 0, subject: "other.test")).Rule);
    }

    [Fact]
    public async Task Service_audits_each_decision_once_and_hands_split_votes_to_the_owner()
    {
        var audit = new RecordingAudit();
        var handOff = new RecordingHandOff();
        var service = new SiteAccessService(audit, handOff, Fixtures.Wrap(new AccountApprovalOptions()), new FixedTime(Fixtures.Now));

        await service.DecideAsync(Triage(), Vote(3, 3, 0, 0), TestContext.Current.CancellationToken);
        await service.DecideAsync(Triage(), Vote(3, 2, 1, 0), TestContext.Current.CancellationToken);
        await service.DecideAsync(Triage(ServiceTermsStatus.Unverified), Vote(3, 3, 0, 0), TestContext.Current.CancellationToken);
        await service.DecideAsync(Triage(), Vote(3, 3, 0, 0, finished: false), TestContext.Current.CancellationToken);

        var rows = audit.OfKind("site_access.decision");
        Assert.Equal(3, rows.Length);
        Assert.Single(handOff.Requests);
        using var doc = JsonDocument.Parse(rows[1].Payload);
        Assert.Equal("example.test", doc.RootElement.GetProperty("site").GetString());
        Assert.Equal("NeedsOwner", doc.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("SplitVote", doc.RootElement.GetProperty("rule").GetString());
        Assert.Equal("vote-1", doc.RootElement.GetProperty("voteReference").GetString());
    }
}
