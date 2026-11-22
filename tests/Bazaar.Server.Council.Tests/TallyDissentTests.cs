using Bazaar.Server.Council;

namespace Bazaar.Server.Tests.Council;

/// <summary>Follow-up on dissent for high-impact ordinary motions (docs/design/10-council-and-voting.md, Follow-up on dissent).</summary>
public class TallyDissentTests
{
    private static HashSet<string> Electorate(int count) =>
        Enumerable.Range(1, count).Select(i => $"agent-{i}").ToHashSet();

    private static Ballot Independent(string voter, string choice) =>
        new(voter, choice, "reason", BallotPhase.Independent);

    private static Ballot FollowUp(string voter, string choice) =>
        new(voter, choice, "reason", BallotPhase.FollowUp);

    /// <summary>Approve/reject ballots: the first <paramref name="yes"/> voters say yes, the next <paramref name="no"/> say no, the rest abstain.</summary>
    private static List<Ballot> ApproveBallots(int yes, int no, int abstain)
    {
        var ballots = new List<Ballot>();
        var voter = 1;
        for (var i = 0; i < yes; i++) ballots.Add(Independent($"agent-{voter++}", ApprovalChoices.Yes));
        for (var i = 0; i < no; i++) ballots.Add(Independent($"agent-{voter++}", ApprovalChoices.No));
        for (var i = 0; i < abstain; i++) ballots.Add(Independent($"agent-{voter++}", ApprovalChoices.Abstain));
        return ballots;
    }

    private static TallyRequest HighImpactApprove(
        int electorate,
        IReadOnlyList<Ballot> ballots,
        decimal share,
        bool highImpact = true,
        TallyRound round = TallyRound.Independent,
        MotionClass motion = MotionClass.Ordinary) =>
        new(VoteKind.ApproveReject, motion, round, Electorate(electorate), ballots, [], Tally.DefaultQuorumMin, highImpact, share);

    [Fact]
    public void Dissent_exactly_at_the_threshold_sends_a_high_impact_motion_to_follow_up()
    {
        // 3 yes, 1 no: dissent 1/4 = 0.25.
        var result = Tally.Decide(HighImpactApprove(4, ApproveBallots(3, 1, 0), share: 0.25m));

        Assert.Equal(TallyDecisionKind.FollowUpRequired, result.Kind);
        Assert.Null(result.Choice);
    }

    [Fact]
    public void Dissent_above_the_threshold_sends_a_high_impact_motion_to_follow_up()
    {
        var result = Tally.Decide(HighImpactApprove(4, ApproveBallots(3, 1, 0), share: 0.20m));

        Assert.Equal(TallyDecisionKind.FollowUpRequired, result.Kind);
    }

    [Fact]
    public void Dissent_below_the_threshold_keeps_the_decided_result()
    {
        var result = Tally.Decide(HighImpactApprove(4, ApproveBallots(3, 1, 0), share: 0.30m));

        Assert.Equal(TallyDecisionKind.Decided, result.Kind);
        Assert.Equal(ApprovalChoices.Yes, result.Choice);
        Assert.Null(result.Reason);
    }

    [Fact]
    public void Default_threshold_of_0_33_triggers_on_one_dissent_in_three()
    {
        // 2 yes, 1 no: dissent 1/3 = 0.3333..., above 0.33.
        var result = Tally.Decide(HighImpactApprove(3, ApproveBallots(2, 1, 0), share: Tally.DefaultFollowupDissentShare));

        Assert.Equal(TallyDecisionKind.FollowUpRequired, result.Kind);
    }

    [Fact]
    public void Default_threshold_keeps_a_quarter_dissent_decided()
    {
        var result = Tally.Decide(HighImpactApprove(4, ApproveBallots(3, 1, 0), share: Tally.DefaultFollowupDissentShare));

        Assert.Equal(TallyDecisionKind.Decided, result.Kind);
    }

    [Fact]
    public void Abstentions_are_excluded_from_the_dissent_denominator()
    {
        // 3 yes, 1 no, 4 abstain. Dissent over non-abstaining ballots is 1/4 = 0.25. Counting abstentions would give 1/8 = 0.125.
        var result = Tally.Decide(HighImpactApprove(8, ApproveBallots(3, 1, 4), share: 0.20m));

        Assert.Equal(TallyDecisionKind.FollowUpRequired, result.Kind);
    }

    [Fact]
    public void Abstentions_do_not_create_dissent()
    {
        // 3 yes and 2 abstain: no ballot dissents, so even a zero threshold does not trigger.
        var result = Tally.Decide(HighImpactApprove(5, ApproveBallots(3, 0, 2), share: 0m));

        Assert.Equal(TallyDecisionKind.Decided, result.Kind);
        Assert.Equal(ApprovalChoices.Yes, result.Choice);
    }

    [Fact]
    public void A_zero_threshold_triggers_on_any_dissent()
    {
        var result = Tally.Decide(HighImpactApprove(4, ApproveBallots(3, 1, 0), share: 0m));

        Assert.Equal(TallyDecisionKind.FollowUpRequired, result.Kind);
    }

    [Fact]
    public void A_threshold_of_one_never_triggers()
    {
        var result = Tally.Decide(HighImpactApprove(3, ApproveBallots(2, 1, 0), share: 1m));

        Assert.Equal(TallyDecisionKind.Decided, result.Kind);
    }

    [Fact]
    public void A_motion_without_the_high_impact_flag_is_unchanged()
    {
        var result = Tally.Decide(HighImpactApprove(4, ApproveBallots(3, 1, 0), share: 0m, highImpact: false));

        Assert.Equal(TallyDecisionKind.Decided, result.Kind);
        Assert.Equal(ApprovalChoices.Yes, result.Choice);
    }

    [Fact]
    public void The_dissent_rule_does_not_apply_in_the_follow_up_round()
    {
        // Final choices after follow-up: 3 yes, 1 no. The one follow-up round is the last step, so this decides.
        var ballots = new[]
        {
            Independent("agent-1", ApprovalChoices.Yes),
            Independent("agent-2", ApprovalChoices.No),
            FollowUp("agent-1", ApprovalChoices.Yes),
            FollowUp("agent-2", ApprovalChoices.Yes),
            FollowUp("agent-3", ApprovalChoices.Yes),
            FollowUp("agent-4", ApprovalChoices.No),
        };

        var result = Tally.Decide(HighImpactApprove(4, ballots, share: 0m, round: TallyRound.FollowUp));

        Assert.Equal(TallyDecisionKind.Decided, result.Kind);
        Assert.Equal(ApprovalChoices.Yes, result.Choice);
    }

    [Fact]
    public void Dissent_on_an_option_choice_motion_counts_ballots_for_other_options()
    {
        // Three ballots for A and one for B: dissent 1/4 = 0.25, at the threshold.
        var ballots = new[]
        {
            Independent("agent-1", "A"),
            Independent("agent-2", "A"),
            Independent("agent-3", "A"),
            Independent("agent-4", "B"),
        };
        var request = new TallyRequest(
            VoteKind.OptionChoice, MotionClass.Ordinary, TallyRound.Independent, Electorate(4), ballots, ["A", "B", "C"],
            Tally.DefaultQuorumMin, HighImpact: true, FollowupDissentShare: 0.25m);

        var result = Tally.Decide(request);

        Assert.Equal(TallyDecisionKind.FollowUpRequired, result.Kind);
    }

    [Fact]
    public void A_bigger_change_keeps_the_unanimity_rule_when_flagged_high_impact()
    {
        // Unanimous yes with a zero threshold: still decided, because bigger changes do not use the dissent rule.
        var unanimous = Tally.Decide(HighImpactApprove(3, ApproveBallots(3, 0, 0), share: 0m, motion: MotionClass.BiggerChange));
        Assert.Equal(TallyDecisionKind.Decided, unanimous.Kind);

        // A split goes to follow-up in the independent round, whatever the threshold says.
        var split = Tally.Decide(HighImpactApprove(4, ApproveBallots(3, 1, 0), share: 1m, motion: MotionClass.BiggerChange));
        Assert.Equal(TallyDecisionKind.FollowUpRequired, split.Kind);

        // After follow-up, a split escalates as not unanimous.
        var afterFollowUp = Tally.Decide(
            HighImpactApprove(4, ApproveBallots(3, 1, 0), share: 1m, round: TallyRound.FollowUp, motion: MotionClass.BiggerChange));
        Assert.Equal(TallyDecisionKind.Escalated, afterFollowUp.Kind);
        Assert.Equal(EscalationReason.NotUnanimous, afterFollowUp.Reason);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    public void A_dissent_share_outside_zero_to_one_is_rejected(double share)
    {
        var request = HighImpactApprove(3, ApproveBallots(2, 1, 0), share: (decimal)share);

        Assert.Throws<ArgumentException>(() => Tally.Decide(request));
    }
}
