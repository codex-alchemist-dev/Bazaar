using Bazaar.Server.Council;
using Bazaar.Server.Council.Membership;
using Bazaar.Server.Council.Service;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bazaar.Server.Council.Tests.Service;

public class CouncilMembershipHandoffTests
{
    private static readonly CancellationToken _ct = CancellationToken.None;

    private static async Task<CouncilVoteRecord> VoteOf(CouncilHarness h, Guid voteId) =>
        (await h.Store.FindVoteAsync(voteId, _ct))!;

    private static async Task<Guid> OpenAsync(
        CouncilHarness h,
        MotionClass motionClass = MotionClass.Ordinary,
        MembershipMotionKind kind = MembershipMotionKind.Hire)
    {
        h.AddAgents("ada", "cal", "dot", "eve");
        h.AddOwner();
        return await h.CreateService().OpenAsync(
            "ada",
            h.Motion(motionClass: motionClass, membershipSubject: "template-7", membershipKind: kind),
            _ct);
    }

    private static async Task VoteAsync(CouncilHarness h, Guid id, params string[] choices)
    {
        var service = h.CreateService();
        var voters = new[] { "cal", "dot", "eve" };
        for (var i = 0; i < choices.Length; i++)
        {
            await service.SubmitBallotAsync(id, voters[i], Ballots.Independent(voters[i], choices[i]), _ct);
        }
    }

    [Fact]
    public async Task A_tally_decided_membership_vote_publishes_one_outcome_to_every_sink_and_is_marked_delivered()
    {
        var h = new CouncilHarness();
        var first = new RecordingSink();
        var second = new RecordingSink();
        h.Sinks.Add(first);
        h.Sinks.Add(second);
        var id = await OpenAsync(h, kind: MembershipMotionKind.Promote);

        await VoteAsync(h, id, "yes", "yes", "no");

        var outcome = Assert.Single(first.Outcomes);
        Assert.Equal(outcome, Assert.Single(second.Outcomes));
        Assert.Equal(id, outcome.VoteId);
        Assert.Equal("template-7", outcome.SubjectAgentId);
        Assert.Equal(MembershipMotionKind.Promote, outcome.Kind);
        Assert.Equal(MembershipDecision.Approved, outcome.Decision);
        Assert.Equal(ApprovalChoices.Yes, outcome.Choice);
        Assert.Equal(1, outcome.VaultDecisionNo);
        Assert.Equal(MembershipOutcomeMapper.ReasonsSummaryRefFor(id), outcome.ReasonsSummaryRef);
        Assert.NotNull((await VoteOf(h, id)).MembershipOutcomeDeliveredAt);
    }

    [Fact]
    public async Task A_rejected_membership_vote_publishes_a_rejected_outcome()
    {
        var h = new CouncilHarness();
        var sink = new RecordingSink();
        h.Sinks.Add(sink);
        var id = await OpenAsync(h);

        await VoteAsync(h, id, "no", "no", "yes");

        var outcome = Assert.Single(sink.Outcomes);
        Assert.Equal(MembershipDecision.Rejected, outcome.Decision);
        Assert.Equal(ApprovalChoices.No, outcome.Choice);
    }

    [Fact]
    public async Task An_owner_resolved_membership_vote_publishes_an_owner_outcome()
    {
        var h = new CouncilHarness();
        var sink = new RecordingSink();
        h.Sinks.Add(sink);
        var id = await OpenAsync(h, MotionClass.BiggerChange);
        await VoteAsync(h, id, "yes", "yes", "no");
        var service = h.CreateService();
        await service.SubmitBallotAsync(id, "cal", Ballots.FollowUp("cal", "yes"), _ct);
        await service.SubmitBallotAsync(id, "dot", Ballots.FollowUp("dot", "yes"), _ct);
        await service.SubmitBallotAsync(id, "eve", Ballots.FollowUp("eve", "no"), _ct);
        Assert.Equal(VoteState.Escalated, (await VoteOf(h, id)).State);
        Assert.Empty(sink.Outcomes);

        await h.CreateService().ResolveEscalationAsync(id, ApprovalChoices.No, "The owner declines.", _ct);

        var outcome = Assert.Single(sink.Outcomes);
        Assert.Equal(MembershipDecision.OwnerRejected, outcome.Decision);
        Assert.Equal(ApprovalChoices.No, outcome.Choice);
        Assert.NotNull((await VoteOf(h, id)).MembershipOutcomeDeliveredAt);
    }

    [Fact]
    public async Task A_unanimous_bigger_change_publishes_an_approved_outcome()
    {
        var h = new CouncilHarness();
        var sink = new RecordingSink();
        h.Sinks.Add(sink);
        var id = await OpenAsync(h, MotionClass.BiggerChange);

        await VoteAsync(h, id, "yes", "yes", "yes");

        Assert.Equal(MembershipDecision.Approved, Assert.Single(sink.Outcomes).Decision);
    }

    [Fact]
    public async Task A_vote_that_is_not_a_membership_vote_publishes_nothing()
    {
        var h = new CouncilHarness();
        var sink = new RecordingSink();
        h.Sinks.Add(sink);
        h.AddThreeVoters();
        var id = await h.CreateService().OpenAsync("ada", h.Motion(), _ct);

        await VoteAsync(h, id, "yes", "yes", "no");

        Assert.Equal(VoteState.Decided, (await VoteOf(h, id)).State);
        Assert.Empty(sink.Outcomes);
        Assert.Null((await VoteOf(h, id)).MembershipOutcomeDeliveredAt);
    }

    [Fact]
    public async Task A_membership_vote_with_no_sink_is_decided_and_left_undelivered()
    {
        var h = new CouncilHarness();
        var id = await OpenAsync(h);

        await VoteAsync(h, id, "yes", "yes", "no");

        Assert.Equal(VoteState.Decided, (await VoteOf(h, id)).State);
        Assert.Single(h.Events.OfKind("council.decision"));
        Assert.Null((await VoteOf(h, id)).MembershipOutcomeDeliveredAt);
    }

    [Fact]
    public async Task A_failing_sink_keeps_the_decision_leaves_the_vote_undelivered_and_other_sinks_still_run()
    {
        var h = new CouncilHarness();
        var failing = new RecordingSink { Fail = true };
        var healthy = new RecordingSink();
        h.Sinks.Add(failing);
        h.Sinks.Add(healthy);
        var id = await OpenAsync(h);

        await VoteAsync(h, id, "yes", "yes", "no");

        var vote = await VoteOf(h, id);
        Assert.Equal(VoteState.Decided, vote.State);
        Assert.Null(vote.MembershipOutcomeDeliveredAt);
        Assert.Single(h.Events.OfKind("council.decision"));
        Assert.Single(healthy.Outcomes);
    }

    [Fact]
    public async Task Redriving_after_a_sink_failure_delivers_once_and_a_further_redrive_applies_nothing()
    {
        var h = new CouncilHarness();
        var sink = new RecordingSink { Fail = true };
        h.Sinks.Add(sink);
        var id = await OpenAsync(h);
        await VoteAsync(h, id, "yes", "yes", "no");
        var firstAttempt = sink.Attempts;

        sink.Fail = false;
        await h.CreateService().RedriveDecisionAsync(id, _ct);
        Assert.Equal(firstAttempt + 1, sink.Attempts);
        Assert.NotNull((await VoteOf(h, id)).MembershipOutcomeDeliveredAt);

        await h.CreateService().RedriveDecisionAsync(id, _ct);
        Assert.Equal(firstAttempt + 1, sink.Attempts);
        Assert.Single(h.Events.OfKind("council.decision"));
    }

    [Fact]
    public async Task A_redriven_outcome_equals_the_first_one()
    {
        var h = new CouncilHarness();
        var sink = new RecordingSink { Fail = true };
        h.Sinks.Add(sink);
        var id = await OpenAsync(h);
        await VoteAsync(h, id, "yes", "yes", "no");

        h.Clock.Now = h.Clock.Now.AddHours(5);
        sink.Fail = false;
        await h.CreateService().RedriveDecisionAsync(id, _ct);

        Assert.Equal(2, sink.Attempts);
        Assert.Equal(sink.AttemptedOutcomes[0], sink.AttemptedOutcomes[1]);
    }

    [Fact]
    public async Task The_sweeper_redrives_an_undelivered_membership_decision_older_than_the_lookback()
    {
        var h = new CouncilHarness();
        var sink = new RecordingSink { Fail = true };
        h.Sinks.Add(sink);
        var id = await OpenAsync(h);
        await VoteAsync(h, id, "yes", "yes", "no");
        Assert.Null((await VoteOf(h, id)).MembershipOutcomeDeliveredAt);

        h.Clock.Now = h.Clock.Now.AddDays(3);
        sink.Fail = false;
        var sweeper = new CouncilDeadlineSweeper(
            h.Store, h.CreateService(), h.Clock, new CouncilSweepOptions(), NullLogger<CouncilDeadlineSweeper>.Instance);
        var result = await sweeper.RunOnceAsync(_ct);

        Assert.Equal(1, result.DecisionsRedriven);
        Assert.Equal(0, result.Failed);
        Assert.Single(sink.Outcomes);
        Assert.NotNull((await VoteOf(h, id)).MembershipOutcomeDeliveredAt);

        // Delivered: the next pass leaves it alone.
        Assert.Equal(0, (await sweeper.RunOnceAsync(_ct)).DecisionsRedriven);
        Assert.Single(sink.Outcomes);
    }

    [Fact]
    public async Task Open_stores_the_membership_kind_and_subject()
    {
        var h = new CouncilHarness();
        var id = await OpenAsync(h, kind: MembershipMotionKind.Retire);

        var vote = await VoteOf(h, id);
        Assert.Equal(MembershipMotionKind.Retire, vote.MembershipKind);
        Assert.Equal("template-7", vote.MembershipSubjectId);
        Assert.Null(vote.MembershipOutcomeDeliveredAt);
    }

    [Fact]
    public async Task Open_requires_the_membership_kind_and_subject_together()
    {
        var h = new CouncilHarness();
        h.AddAgents("ada", "cal", "dot", "eve");
        var service = h.CreateService();

        var kindOnly = h.Motion() with { MembershipKind = MembershipMotionKind.Hire };
        var subjectOnly = h.Motion() with { MembershipSubjectId = "template-7" };

        await Assert.ThrowsAsync<ArgumentException>(() => service.OpenAsync("ada", kindOnly, _ct));
        await Assert.ThrowsAsync<ArgumentException>(() => service.OpenAsync("ada", subjectOnly, _ct));
    }

    private sealed class RecordingSink : IMembershipOutcomeSink
    {
        public bool Fail { get; set; }

        public int Attempts { get; private set; }

        public List<MembershipOutcome> AttemptedOutcomes { get; } = [];

        public List<MembershipOutcome> Outcomes { get; } = [];

        public Task PublishAsync(MembershipOutcome outcome, CancellationToken ct)
        {
            Attempts++;
            AttemptedOutcomes.Add(outcome);
            if (Fail)
            {
                throw new IOException("Simulated sink failure.");
            }

            Outcomes.Add(outcome);
            return Task.CompletedTask;
        }
    }
}
