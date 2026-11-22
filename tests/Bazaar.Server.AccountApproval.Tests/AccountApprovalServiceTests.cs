using System.Text.Json;
using Bazaar.Core;
using Bazaar.Server.AccountApproval.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.AccountApproval.Tests;

public class AccountApprovalServiceTests
{
    private static readonly string[] _voters = ["agent-a", "agent-b", "agent-c"];

    [Fact]
    public async Task Two_approvals_approve_and_emit_one_decision_row_and_two_vote_rows()
    {
        var h = new Harness();
        Assert.IsType<Pending>(await h.Service.SubmitAsync(Fixtures.Request(), TestContext.Current.CancellationToken));

        var first = await h.Service.RecordVoteAsync(Fixtures.Vote("agent-a"), TestContext.Current.CancellationToken);
        var second = await h.Service.RecordVoteAsync(Fixtures.Vote("agent-b"), TestContext.Current.CancellationToken);

        Assert.Equal(VoteDisposition.Counted, first.Disposition);
        Assert.IsType<Pending>(first.Decision);
        Assert.IsType<ApprovedByPeers>(second.Decision);
        Assert.Equal(2, h.Audit.OfKind("account_approval.peer_vote").Length);
        var decision = Assert.Single(h.Audit.OfKind("account_approval.decision"));
        Assert.Equal("agent-r", decision.Actor);
        using var doc = JsonDocument.Parse(decision.Payload);
        Assert.Equal("req-1", doc.RootElement.GetProperty("requestId").GetString());
        Assert.Equal("example.test", doc.RootElement.GetProperty("service").GetString());
        Assert.Equal("agent-r", doc.RootElement.GetProperty("requester").GetString());
        Assert.Equal("ApprovedByPeers", doc.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("PeerQuorumReached", doc.RootElement.GetProperty("rule").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("approvers").GetArrayLength());
        Assert.Empty(h.HandOff.Requests);
    }

    [Fact]
    public async Task Vote_rows_carry_approver_model_and_reasoning_hash()
    {
        var h = new Harness();
        await h.Service.SubmitAsync(Fixtures.Request(), TestContext.Current.CancellationToken);

        await h.Service.RecordVoteAsync(Fixtures.Vote("agent-a"), TestContext.Current.CancellationToken);

        using var doc = JsonDocument.Parse(Assert.Single(h.Audit.OfKind("account_approval.peer_vote")).Payload);
        Assert.Equal("agent-a", doc.RootElement.GetProperty("approver").GetString());
        Assert.Equal("model-x", doc.RootElement.GetProperty("model").GetString());
        Assert.Equal("hash-1", doc.RootElement.GetProperty("reasoningHash").GetString());
        Assert.Equal("Approve", doc.RootElement.GetProperty("vote").GetString());
    }

    [Fact]
    public async Task A_refusal_hands_off_to_the_owner_once_with_one_decision_row()
    {
        var h = new Harness();
        await h.Service.SubmitAsync(Fixtures.Request(), TestContext.Current.CancellationToken);
        await h.Service.RecordVoteAsync(Fixtures.Vote("agent-a"), TestContext.Current.CancellationToken);

        var refusal = await h.Service.RecordVoteAsync(Fixtures.Vote("agent-b", PeerVoteKind.Refuse), TestContext.Current.CancellationToken);

        Assert.Equal(DecisionRule.PeerRefused, Assert.IsType<NeedsOwner>(refusal.Decision).Rule);
        Assert.Single(h.HandOff.Requests);
        Assert.Single(h.Audit.OfKind("account_approval.decision"));
    }

    [Fact]
    public async Task An_approval_after_a_refusal_does_not_resurrect_the_request()
    {
        var h = new Harness();
        await h.Service.SubmitAsync(Fixtures.Request(), TestContext.Current.CancellationToken);
        await h.Service.RecordVoteAsync(Fixtures.Vote("agent-a", PeerVoteKind.Refuse), TestContext.Current.CancellationToken);

        var late1 = await h.Service.RecordVoteAsync(Fixtures.Vote("agent-b"), TestContext.Current.CancellationToken);
        var late2 = await h.Service.RecordVoteAsync(Fixtures.Vote("agent-c"), TestContext.Current.CancellationToken);

        Assert.Equal(VoteDisposition.AlreadyDecided, late1.Disposition);
        Assert.Equal(VoteDisposition.AlreadyDecided, late2.Disposition);
        Assert.IsType<NeedsOwner>(await h.Service.GetAsync("req-1", TestContext.Current.CancellationToken));
        Assert.Single(h.Audit.OfKind("account_approval.decision"));
        Assert.Single(h.HandOff.Requests);
    }

    [Fact]
    public async Task A_vote_after_approval_changes_nothing()
    {
        var h = new Harness();
        await h.Service.SubmitAsync(Fixtures.Request(), TestContext.Current.CancellationToken);
        await h.Service.RecordVoteAsync(Fixtures.Vote("agent-a"), TestContext.Current.CancellationToken);
        await h.Service.RecordVoteAsync(Fixtures.Vote("agent-b"), TestContext.Current.CancellationToken);

        var late = await h.Service.RecordVoteAsync(Fixtures.Vote("agent-c", PeerVoteKind.Refuse), TestContext.Current.CancellationToken);

        Assert.Equal(VoteDisposition.AlreadyDecided, late.Disposition);
        Assert.IsType<ApprovedByPeers>(await h.Service.GetAsync("req-1", TestContext.Current.CancellationToken));
        Assert.Empty(h.HandOff.Requests);
    }

    [Fact]
    public async Task A_duplicate_approval_is_counted_once_and_not_audited_again()
    {
        var h = new Harness();
        await h.Service.SubmitAsync(Fixtures.Request(), TestContext.Current.CancellationToken);

        await h.Service.RecordVoteAsync(Fixtures.Vote("agent-a"), TestContext.Current.CancellationToken);
        var dup = await h.Service.RecordVoteAsync(Fixtures.Vote("agent-a"), TestContext.Current.CancellationToken);

        Assert.Equal(VoteDisposition.Duplicate, dup.Disposition);
        Assert.Equal(1, Assert.IsType<Pending>(dup.Decision).Remaining);
        Assert.Single(h.Audit.OfKind("account_approval.peer_vote"));
    }

    [Fact]
    public async Task A_vote_for_a_different_request_id_is_rejected()
    {
        var h = new Harness();
        await h.Service.SubmitAsync(Fixtures.Request(), TestContext.Current.CancellationToken);

        var result = await h.Service.RecordVoteAsync(Fixtures.Vote("agent-a", id: "req-other"), TestContext.Current.CancellationToken);

        Assert.Equal(VoteDisposition.UnknownRequest, result.Disposition);
        Assert.Null(result.Decision);
        Assert.Empty(h.Audit.OfKind("account_approval.peer_vote"));
    }

    [Fact]
    public async Task A_vote_bound_to_a_different_service_is_rejected()
    {
        var h = new Harness();
        await h.Service.SubmitAsync(Fixtures.Request(), TestContext.Current.CancellationToken);

        var result = await h.Service.RecordVoteAsync(Fixtures.Vote("agent-a", service: "other.test"), TestContext.Current.CancellationToken);

        Assert.Equal(VoteDisposition.BindingMismatch, result.Disposition);
        Assert.Equal(2, Assert.IsType<Pending>(await h.Service.GetAsync("req-1", TestContext.Current.CancellationToken)).Remaining);
    }

    [Fact]
    public async Task Requester_self_approval_is_ineligible_and_does_not_count()
    {
        var h = new Harness();
        await h.Service.SubmitAsync(Fixtures.Request(requester: "agent-a"), TestContext.Current.CancellationToken);

        var self = await h.Service.RecordVoteAsync(Fixtures.Vote("agent-a"), TestContext.Current.CancellationToken);
        await h.Service.RecordVoteAsync(Fixtures.Vote("agent-b"), TestContext.Current.CancellationToken);

        Assert.Equal(VoteDisposition.Ineligible, self.Disposition);
        Assert.Equal(1, Assert.IsType<Pending>(await h.Service.GetAsync("req-1", TestContext.Current.CancellationToken)).Remaining);
    }

    [Fact]
    public async Task Tainted_and_non_frontier_approvers_are_ineligible()
    {
        var h = new Harness();
        await h.Service.SubmitAsync(Fixtures.Request(), TestContext.Current.CancellationToken);
        h.Directory.Standings["agent-a"] = new ApproverStanding(false, true, true);
        h.Directory.Standings["agent-b"] = new ApproverStanding(true, false, true);

        Assert.Equal(VoteDisposition.Ineligible, (await h.Service.RecordVoteAsync(Fixtures.Vote("agent-a"), TestContext.Current.CancellationToken)).Disposition);
        Assert.Equal(VoteDisposition.Ineligible, (await h.Service.RecordVoteAsync(Fixtures.Vote("agent-b"), TestContext.Current.CancellationToken)).Disposition);
        Assert.Equal(VoteDisposition.Ineligible, (await h.Service.RecordVoteAsync(Fixtures.Vote("agent-x"), TestContext.Current.CancellationToken)).Disposition);
    }

    [Fact]
    public async Task Too_few_eligible_approvers_goes_to_the_owner_at_submit()
    {
        var h = new Harness(allGood: false);
        h.Directory.Standings["agent-a"] = Fixtures.Good;

        var decision = await h.Service.SubmitAsync(Fixtures.Request(), TestContext.Current.CancellationToken);

        Assert.Equal(DecisionRule.TooFewEligiblePeers, Assert.IsType<NeedsOwner>(decision).Rule);
        Assert.Single(h.HandOff.Requests);
        Assert.Single(h.Audit.OfKind("account_approval.decision"));
    }

    [Fact]
    public async Task No_directory_data_means_nobody_is_eligible_and_the_owner_decides()
    {
        var h = new Harness(allGood: false);

        Assert.IsType<NeedsOwner>(await h.Service.SubmitAsync(Fixtures.Request(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_throwing_directory_fails_closed_to_the_owner()
    {
        var h = new Harness();
        h.Directory.Throw = true;

        Assert.IsType<NeedsOwner>(await h.Service.SubmitAsync(Fixtures.Request(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_shipped_default_has_no_listed_approvers_so_the_owner_decides()
    {
        var service = new AccountApprovalService(new NoApproverDirectory(), new RecordingAudit(), new RecordingHandOff(), new InMemoryAccountApprovalStore(), Fixtures.Wrap(new AccountApprovalOptions()), new FixedTime(Fixtures.Now));

        Assert.IsType<NeedsOwner>(await service.SubmitAsync(Fixtures.Request(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Deny_cases_emit_one_denied_row_and_no_owner_hand_off()
    {
        var h = new Harness();

        var decision = await h.Service.SubmitAsync(Fixtures.Request(terms: ServiceTermsStatus.Unverified), TestContext.Current.CancellationToken);

        Assert.Equal(DecisionRule.UnverifiedTerms, Assert.IsType<Denied>(decision).Rule);
        Assert.Single(h.Audit.OfKind("account_approval.decision"));
        Assert.Empty(h.HandOff.Requests);
    }

    [Fact]
    public async Task Resubmitting_returns_the_current_decision_without_new_rows()
    {
        var h = new Harness();
        await h.Service.SubmitAsync(Fixtures.Request(spend: true), TestContext.Current.CancellationToken);

        var again = await h.Service.SubmitAsync(Fixtures.Request(spend: true), TestContext.Current.CancellationToken);

        Assert.IsType<NeedsOwner>(again);
        Assert.Single(h.Audit.OfKind("account_approval.decision"));
        Assert.Single(h.HandOff.Requests);
    }

    [Fact]
    public async Task Reusing_a_request_id_for_another_request_throws()
    {
        var h = new Harness();
        await h.Service.SubmitAsync(Fixtures.Request(), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.SubmitAsync(Fixtures.Request(service: "other.test"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Reevaluate_sends_an_expired_pending_request_to_the_owner()
    {
        var h = new Harness();
        await h.Service.SubmitAsync(Fixtures.Request(), TestContext.Current.CancellationToken);
        await h.Service.RecordVoteAsync(Fixtures.Vote("agent-a"), TestContext.Current.CancellationToken);

        h.Time.Current = Fixtures.Now.AddHours(25);
        var decision = await h.Service.ReevaluateAsync("req-1", TestContext.Current.CancellationToken);

        Assert.Equal(DecisionRule.ApprovalExpired, Assert.IsType<NeedsOwner>(decision).Rule);
        Assert.Single(h.HandOff.Requests);
    }

    [Fact]
    public async Task A_failing_audit_leaves_the_request_undecided()
    {
        var h = new Harness();
        await h.Service.SubmitAsync(Fixtures.Request(), TestContext.Current.CancellationToken);
        await h.Service.RecordVoteAsync(Fixtures.Vote("agent-a"), TestContext.Current.CancellationToken);

        h.Audit.Throw = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.RecordVoteAsync(Fixtures.Vote("agent-b"), TestContext.Current.CancellationToken));
        h.Audit.Throw = false;

        Assert.IsType<Pending>(await h.Service.GetAsync("req-1", TestContext.Current.CancellationToken));
        Assert.IsType<ApprovedByPeers>((await h.Service.RecordVoteAsync(Fixtures.Vote("agent-b"), TestContext.Current.CancellationToken)).Decision);
    }

    [Fact]
    public async Task Parallel_duplicate_approvals_count_once()
    {
        var h = new Harness();
        await h.Service.SubmitAsync(Fixtures.Request(), TestContext.Current.CancellationToken);

        var results = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(() => h.Service.RecordVoteAsync(Fixtures.Vote("agent-a"), TestContext.Current.CancellationToken), TestContext.Current.CancellationToken)));

        Assert.Equal(1, results.Count(r => r.Disposition == VoteDisposition.Counted));
        Assert.Equal(49, results.Count(r => r.Disposition == VoteDisposition.Duplicate));
        Assert.Equal(1, Assert.IsType<Pending>(await h.Service.GetAsync("req-1", TestContext.Current.CancellationToken)).Remaining);
        Assert.Single(h.Audit.OfKind("account_approval.peer_vote"));
    }

    [Fact]
    public async Task Parallel_approvals_reach_the_quorum_exactly_once()
    {
        var h = new Harness();
        await h.Service.SubmitAsync(Fixtures.Request(), TestContext.Current.CancellationToken);

        await Task.WhenAll(_voters.SelectMany(a => Enumerable.Range(0, 10).Select(_ => Task.Run(() => h.Service.RecordVoteAsync(Fixtures.Vote(a), TestContext.Current.CancellationToken), TestContext.Current.CancellationToken))));

        Assert.IsType<ApprovedByPeers>(await h.Service.GetAsync("req-1", TestContext.Current.CancellationToken));
        Assert.Single(h.Audit.OfKind("account_approval.decision"));
        Assert.Empty(h.HandOff.Requests);
    }

    [Fact]
    public async Task An_approval_racing_a_refusal_ends_in_exactly_one_consistent_outcome()
    {
        for (var i = 0; i < 25; i++)
        {
            var h = new Harness();
            await h.Service.SubmitAsync(Fixtures.Request(), TestContext.Current.CancellationToken);
            await h.Service.RecordVoteAsync(Fixtures.Vote("agent-a"), TestContext.Current.CancellationToken);

            await Task.WhenAll(
                Task.Run(() => h.Service.RecordVoteAsync(Fixtures.Vote("agent-b"), TestContext.Current.CancellationToken), TestContext.Current.CancellationToken),
                Task.Run(() => h.Service.RecordVoteAsync(Fixtures.Vote("agent-c", PeerVoteKind.Refuse), TestContext.Current.CancellationToken), TestContext.Current.CancellationToken));

            var final = await h.Service.GetAsync("req-1", TestContext.Current.CancellationToken);
            var row = Assert.Single(h.Audit.OfKind("account_approval.decision"));
            if (final is ApprovedByPeers)
            {
                // Approval won the order; the refusal arrived after and changed nothing.
                Assert.Empty(h.HandOff.Requests);
            }
            else
            {
                Assert.IsType<NeedsOwner>(final);
                Assert.Single(h.HandOff.Requests);
            }

            Assert.Contains(final!.GetType().Name, row.Payload);
        }
    }

    [Fact]
    public void Options_reject_a_quorum_below_two_instead_of_clamping()
    {
        var options = new AccountApprovalOptions { PeerQuorum = 1 };

        Assert.NotEmpty(options.Validate());
        Assert.Throws<OptionsValidationException>(() => new AccountApprovalService(new FakeDirectory(), new RecordingAudit(), new RecordingHandOff(), new InMemoryAccountApprovalStore(), Fixtures.Wrap(options)));
    }

    [Fact]
    public void Options_ship_protective_defaults()
    {
        var options = new AccountApprovalOptions();

        Assert.Empty(options.Validate());
        Assert.Equal(2, options.PeerQuorum);
        Assert.False(options.AgentAcceptsTerms);
        Assert.Empty(options.PeerApprovers);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Options_reject_a_non_positive_expiry(int hours) =>
        Assert.NotEmpty(new AccountApprovalOptions { ApprovalExpiryHours = hours }.Validate());

    [Fact]
    public void Options_reject_blank_approver_ids() =>
        Assert.NotEmpty(new AccountApprovalOptions { PeerApprovers = ["agent-a", " "] }.Validate());

    [Fact]
    public void Component_registers_the_services_and_binds_options()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Bazaar:AccountApproval:PeerQuorum"] = "3",
            ["Bazaar:AccountApproval:PeerApprovers:0"] = "agent-a",
        }).Build();
        var component = new AccountApprovalComponent();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<Bazaar.Server.Security.Audit.IAuditLog>(new RecordingAudit());
        services.AddSingleton<IOwnerApprovalHandOff>(new RecordingHandOff());

        component.AddServices(services, config);
        using var provider = services.BuildServiceProvider();

        Assert.Equal("account-approval", component.Manifest.Id);
        Assert.Equal(ComponentKind.Feature, component.Manifest.Kind);
        Assert.Equal(3, provider.GetRequiredService<IOptions<AccountApprovalOptions>>().Value.PeerQuorum);
        Assert.NotNull(provider.GetRequiredService<AccountApprovalService>());
        Assert.NotNull(provider.GetRequiredService<SiteAccessService>());
        Assert.IsType<NoApproverDirectory>(provider.GetRequiredService<IApproverDirectory>());
    }

    [Fact]
    public void Component_rejects_a_bound_quorum_below_two()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Bazaar:AccountApproval:PeerQuorum"] = "1",
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        new AccountApprovalComponent().AddServices(services, config);
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<AccountApprovalOptions>>().Value);
    }
}
