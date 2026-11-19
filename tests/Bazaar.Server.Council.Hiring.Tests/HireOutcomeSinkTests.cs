using System.Reflection;
using Bazaar.Server.Council;
using Bazaar.Server.Council.Hiring;
using Bazaar.Server.Council.Membership;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Council.Hiring.Tests;

public class HireOutcomeSinkTests
{
    private const string _template = "talent-analyst-v2";

    private readonly InMemoryHireDecisionStore _store = new();
    private readonly FakeTimeProvider _time = new(Samples.Now.AddHours(2));

    private HireOutcomeSink Sink => new(_store, _time);

    /// <summary>Stores a pending hire for the template and returns its vote id.</summary>
    private async Task<Guid> PendingVote(string template = _template)
    {
        var voteId = Guid.NewGuid();
        _store.Seed(new HireVoteState(template, voteId, HireStatus.Pending, null, Samples.Now));
        return voteId;
    }

    private async Task<HireVoteState> Current(string template = _template) =>
        (await _store.FindAsync(template, CancellationToken.None))!;

    [Theory]
    [InlineData(MembershipDecision.Approved, ApprovalChoices.Yes)]
    [InlineData(MembershipDecision.OwnerApproved, ApprovalChoices.Yes)]
    public async Task Explicit_approve_releases_the_template(MembershipDecision decision, string choice)
    {
        var voteId = await PendingVote();
        var outcome = Outcome(voteId, decision, choice);

        await Sink.PublishAsync(outcome, CancellationToken.None);

        var state = await Current();
        Assert.Equal(HireStatus.Released, state.Status);
        Assert.Equal(decision, state.Decision);
        Assert.Equal(_time.GetUtcNow(), state.UpdatedAt);
    }

    [Theory]
    [InlineData(MembershipDecision.Rejected, ApprovalChoices.No)]
    [InlineData(MembershipDecision.OwnerRejected, ApprovalChoices.No)]
    [InlineData(MembershipDecision.Withdrawn, null)]
    public async Task Rejection_and_withdrawal_block_the_template(MembershipDecision decision, string? choice)
    {
        var voteId = await PendingVote();

        await Sink.PublishAsync(Outcome(voteId, decision, choice), CancellationToken.None);

        var state = await Current();
        Assert.Equal(HireStatus.Blocked, state.Status);
        Assert.Equal(decision, state.Decision);
    }

    [Theory]
    [InlineData(MembershipDecision.Approved, null)]
    [InlineData(MembershipDecision.OwnerApproved, ApprovalChoices.No)]
    [InlineData(MembershipDecision.Approved, ApprovalChoices.Abstain)]
    public async Task Approve_without_an_explicit_yes_choice_fails_closed(MembershipDecision decision, string? choice)
    {
        var voteId = await PendingVote();

        await Sink.PublishAsync(Outcome(voteId, decision, choice), CancellationToken.None);

        Assert.Equal(HireStatus.Blocked, (await Current()).Status);
    }

    [Fact]
    public async Task Other_membership_kinds_are_ignored()
    {
        var voteId = await PendingVote();
        var promote = Outcome(voteId, MembershipDecision.Approved, ApprovalChoices.Yes) with { Kind = MembershipMotionKind.Promote };

        await Sink.PublishAsync(promote, CancellationToken.None);

        var state = await Current();
        Assert.Equal(HireStatus.Pending, state.Status);
        Assert.Equal(0, _store.SaveCount);
    }

    [Fact]
    public async Task An_outcome_for_a_vote_other_than_the_current_one_changes_nothing()
    {
        await PendingVote();
        var staleVote = Guid.NewGuid();

        await Sink.PublishAsync(Outcome(staleVote, MembershipDecision.Approved, ApprovalChoices.Yes), CancellationToken.None);

        Assert.Equal(HireStatus.Pending, (await Current()).Status);
        Assert.Equal(0, _store.SaveCount);
    }

    [Fact]
    public async Task An_outcome_for_an_unknown_template_changes_nothing()
    {
        var outcome = Outcome(Guid.NewGuid(), MembershipDecision.Approved, ApprovalChoices.Yes, template: "no-such-template");

        await Sink.PublishAsync(outcome, CancellationToken.None);

        Assert.Null(await _store.FindAsync("no-such-template", CancellationToken.None));
        Assert.Equal(0, _store.SaveCount);
    }

    [Fact]
    public async Task A_replayed_outcome_is_idempotent()
    {
        var voteId = await PendingVote();
        var outcome = Outcome(voteId, MembershipDecision.Approved, ApprovalChoices.Yes);
        await Sink.PublishAsync(outcome, CancellationToken.None);
        var saves = _store.SaveCount;

        await Sink.PublishAsync(outcome, CancellationToken.None);

        Assert.Equal(saves, _store.SaveCount);
        Assert.Equal(HireStatus.Released, (await Current()).Status);
    }

    [Fact]
    public async Task A_late_contrary_outcome_cannot_flip_a_decided_vote()
    {
        var voteId = await PendingVote();
        await Sink.PublishAsync(Outcome(voteId, MembershipDecision.Approved, ApprovalChoices.Yes), CancellationToken.None);

        await Sink.PublishAsync(Outcome(voteId, MembershipDecision.OwnerRejected, ApprovalChoices.No), CancellationToken.None);

        Assert.Equal(HireStatus.Released, (await Current()).Status);
    }

    [Fact]
    public async Task Unanimous_bigger_change_from_the_mapper_releases_the_template()
    {
        var voteId = await PendingVote();
        var counts = new Dictionary<string, int> { [ApprovalChoices.Yes] = 4, [ApprovalChoices.No] = 0, [ApprovalChoices.Abstain] = 0 };
        var motion = new MembershipMotionMetadata(voteId, MembershipMotionKind.Hire, _template, MotionClass.BiggerChange, Samples.Now);
        var decision = new TallyDecision(TallyDecisionKind.Decided, ApprovalChoices.Yes, null, counts);
        var outcome = MembershipOutcomeMapper.Map(decision, motion);

        await Sink.PublishAsync(outcome!, CancellationToken.None);

        Assert.Equal(HireStatus.Released, (await Current()).Status);
    }

    [Fact]
    public async Task Split_bigger_change_stays_pending_and_publishes_nothing()
    {
        var voteId = await PendingVote();
        var counts = new Dictionary<string, int> { [ApprovalChoices.Yes] = 2, [ApprovalChoices.No] = 1, [ApprovalChoices.Abstain] = 0 };
        var motion = new MembershipMotionMetadata(voteId, MembershipMotionKind.Hire, _template, MotionClass.BiggerChange, Samples.Now);
        var escalated = new TallyDecision(TallyDecisionKind.Escalated, null, EscalationReason.NotUnanimous, counts);

        var outcome = MembershipOutcomeMapper.Map(escalated, motion);

        Assert.Null(outcome);
        Assert.Equal(HireStatus.Pending, (await Current()).Status);
    }

    [Fact]
    public async Task Null_outcome_is_rejected()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => Sink.PublishAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task Only_approve_decisions_with_a_yes_choice_release_across_every_decision()
    {
        var released = new List<MembershipDecision>();
        foreach (var decision in Enum.GetValues<MembershipDecision>())
        {
            var voteId = Guid.NewGuid();
            var template = $"template-{decision}";
            _store.Seed(new HireVoteState(template, voteId, HireStatus.Pending, null, Samples.Now));

            await Sink.PublishAsync(Outcome(voteId, decision, ApprovalChoices.Yes, template), CancellationToken.None);

            if ((await Current(template)).Status == HireStatus.Released)
            {
                released.Add(decision);
            }
        }

        Assert.Equal([MembershipDecision.Approved, MembershipDecision.OwnerApproved], released);
    }

    [Fact]
    public void Hire_vote_state_carries_no_tool_grant_field()
    {
        // Tier 2 tool grants are separate approvals (docs/design/25, 15). The hire state must not carry one.
        var names = typeof(HireVoteState).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).Order().ToArray();

        Assert.Equal(["Decision", "Status", "TemplateId", "UpdatedAt", "VoteId"], names);
    }

    private static MembershipOutcome Outcome(
        Guid voteId,
        MembershipDecision decision,
        string? choice,
        string template = _template) =>
        new(voteId, template, MembershipMotionKind.Hire, decision, choice, $"council.decision:{voteId:D}", Samples.Now, null);
}
