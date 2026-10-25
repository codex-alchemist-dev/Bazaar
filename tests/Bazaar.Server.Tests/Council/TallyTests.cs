using Bazaar.Server.Council;

namespace Bazaar.Server.Tests.Council;

public class TallyTests
{
    private static HashSet<string> Electorate(int count) =>
        Enumerable.Range(1, count).Select(i => $"agent-{i}").ToHashSet();

    private static Ballot Independent(string voter, string choice) =>
        new(voter, choice, "reason", BallotPhase.Independent);

    private static Ballot FollowUp(string voter, string choice) =>
        new(voter, choice, "reason", BallotPhase.FollowUp);

    private static TallyRequest Approve(
        int electorate,
        IReadOnlyList<Ballot> ballots,
        TallyRound round = TallyRound.Independent,
        MotionClass motion = MotionClass.Ordinary,
        int quorumMin = Tally.DefaultQuorumMin) =>
        new(VoteKind.ApproveReject, motion, round, Electorate(electorate), ballots, [], quorumMin);

    private static TallyRequest Choose(
        int electorate,
        IReadOnlyList<string> options,
        IReadOnlyList<Ballot> ballots,
        TallyRound round = TallyRound.Independent) =>
        new(VoteKind.OptionChoice, MotionClass.Ordinary, round, Electorate(electorate), ballots, options);

    // Approve/reject, ordinary

    [Fact]
    public void Unanimous_yes_is_decided_in_the_independent_round()
    {
        var ballots = new[] { Independent("agent-1", "yes"), Independent("agent-2", "yes"), Independent("agent-3", "yes") };

        var result = Tally.Decide(Approve(3, ballots));

        Assert.Equal(TallyDecisionKind.Decided, result.Kind);
        Assert.Equal("yes", result.Choice);
        Assert.Null(result.Reason);
    }

    [Fact]
    public void Two_to_one_majority_is_decided_for_an_ordinary_motion()
    {
        var ballots = new[] { Independent("agent-1", "yes"), Independent("agent-2", "yes"), Independent("agent-3", "no") };

        var result = Tally.Decide(Approve(3, ballots));

        Assert.Equal(TallyDecisionKind.Decided, result.Kind);
        Assert.Equal("yes", result.Choice);
    }

    [Fact]
    public void Abstentions_are_excluded_from_the_majority_threshold()
    {
        var ballots = new[] { Independent("agent-1", "yes"), Independent("agent-2", "yes"), Independent("agent-3", "abstain") };

        var result = Tally.Decide(Approve(3, ballots));

        Assert.Equal(TallyDecisionKind.Decided, result.Kind);
        Assert.Equal("yes", result.Choice);
    }

    [Fact]
    public void An_exact_tie_requires_follow_up()
    {
        var ballots = new[] { Independent("agent-1", "yes"), Independent("agent-2", "yes"), Independent("agent-3", "no"), Independent("agent-4", "no") };

        var result = Tally.Decide(Approve(4, ballots));

        Assert.Equal(TallyDecisionKind.FollowUpRequired, result.Kind);
        Assert.Null(result.Choice);
    }

    [Fact]
    public void A_tie_after_follow_up_escalates_to_the_owner()
    {
        var ballots = new[] { Independent("agent-1", "yes"), Independent("agent-2", "no"), FollowUp("agent-1", "no"), FollowUp("agent-2", "yes") };

        var result = Tally.Decide(Approve(2, ballots, TallyRound.FollowUp, quorumMin: 1));

        Assert.Equal(TallyDecisionKind.Escalated, result.Kind);
        Assert.Equal(EscalationReason.Tie, result.Reason);
    }

    [Fact]
    public void Follow_up_ballot_replaces_only_the_independent_ballot_of_the_voter_who_cast_it()
    {
        var ballots = new[]
        {
            Independent("agent-1", "yes"), Independent("agent-2", "yes"),
            Independent("agent-3", "no"), Independent("agent-4", "no"),
            FollowUp("agent-3", "yes"),
        };

        var result = Tally.Decide(Approve(4, ballots, TallyRound.FollowUp));

        Assert.Equal(TallyDecisionKind.Decided, result.Kind);
        Assert.Equal("yes", result.Choice);
        Assert.Equal(3, result.Counts["yes"]);
        Assert.Equal(1, result.Counts["no"]);
    }

    [Fact]
    public void Follow_up_round_without_a_decisive_result_escalates_as_no_decisive_result_when_all_abstain()
    {
        var ballots = new[] { Independent("agent-1", "abstain"), Independent("agent-2", "abstain"), Independent("agent-3", "abstain") };

        var first = Tally.Decide(Approve(3, ballots));
        var second = Tally.Decide(Approve(3, ballots, TallyRound.FollowUp));

        Assert.Equal(TallyDecisionKind.FollowUpRequired, first.Kind);
        Assert.Equal(TallyDecisionKind.Escalated, second.Kind);
        Assert.Equal(EscalationReason.NoDecisiveResult, second.Reason);
    }

    [Fact]
    public void Counts_include_zero_entries_for_every_approve_reject_choice()
    {
        var ballots = new[] { Independent("agent-1", "yes"), Independent("agent-2", "yes"), Independent("agent-3", "yes") };

        var result = Tally.Decide(Approve(3, ballots));

        Assert.Equal(3, result.Counts["yes"]);
        Assert.Equal(0, result.Counts["no"]);
        Assert.Equal(0, result.Counts["abstain"]);
    }

    // Quorum

    [Fact]
    public void Fewer_ballots_than_quorum_minimum_escalate_for_no_quorum()
    {
        var ballots = new[] { Independent("agent-1", "yes"), Independent("agent-2", "yes") };

        var result = Tally.Decide(Approve(5, ballots));

        Assert.Equal(TallyDecisionKind.Escalated, result.Kind);
        Assert.Equal(EscalationReason.NoQuorum, result.Reason);
    }

    [Fact]
    public void Ballots_at_exactly_the_quorum_minimum_proceed_when_they_are_a_majority()
    {
        var ballots = new[] { Independent("agent-1", "yes"), Independent("agent-2", "yes"), Independent("agent-3", "yes") };

        var result = Tally.Decide(Approve(5, ballots));

        Assert.Equal(TallyDecisionKind.Decided, result.Kind);
    }

    [Fact]
    public void Ballots_that_are_not_more_than_half_the_electorate_fail_quorum()
    {
        var ballots = new[] { Independent("agent-1", "yes"), Independent("agent-2", "yes"), Independent("agent-3", "yes") };

        var result = Tally.Decide(Approve(6, ballots));

        Assert.Equal(EscalationReason.NoQuorum, result.Reason);
    }

    [Fact]
    public void Abstentions_count_toward_quorum()
    {
        var ballots = new[] { Independent("agent-1", "abstain"), Independent("agent-2", "abstain"), Independent("agent-3", "abstain") };

        var result = Tally.Decide(Approve(5, ballots));

        Assert.Equal(TallyDecisionKind.FollowUpRequired, result.Kind);
    }

    [Fact]
    public void Quorum_minimum_is_taken_from_the_request()
    {
        var ballots = new[] { Independent("agent-1", "yes"), Independent("agent-2", "yes") };

        var result = Tally.Decide(Approve(3, ballots, quorumMin: 2));

        Assert.Equal(TallyDecisionKind.Decided, result.Kind);
    }

    [Fact]
    public void Empty_electorate_escalates_for_no_quorum()
    {
        var result = Tally.Decide(Approve(0, []));

        Assert.Equal(EscalationReason.NoQuorum, result.Reason);
    }

    // Option choice

    [Fact]
    public void Option_with_more_than_half_of_ballots_is_decided()
    {
        var ballots = new[] { Independent("agent-1", "A"), Independent("agent-2", "A"), Independent("agent-3", "A"), Independent("agent-4", "B"), Independent("agent-5", "C") };

        var result = Tally.Decide(Choose(5, ["A", "B", "C"], ballots));

        Assert.Equal(TallyDecisionKind.Decided, result.Kind);
        Assert.Equal("A", result.Choice);
    }

    [Fact]
    public void Two_two_one_split_requires_follow_up_then_escalates_as_a_tie()
    {
        var ballots = new[] { Independent("agent-1", "A"), Independent("agent-2", "A"), Independent("agent-3", "B"), Independent("agent-4", "B"), Independent("agent-5", "C") };

        var first = Tally.Decide(Choose(5, ["A", "B", "C"], ballots));
        var second = Tally.Decide(Choose(5, ["A", "B", "C"], ballots, TallyRound.FollowUp));

        Assert.Equal(TallyDecisionKind.FollowUpRequired, first.Kind);
        Assert.Equal(TallyDecisionKind.Escalated, second.Kind);
        Assert.Equal(EscalationReason.Tie, second.Reason);
    }

    [Fact]
    public void Non_decisive_split_with_a_single_leader_escalates_as_no_decisive_result()
    {
        var ballots = new[] { Independent("agent-1", "A"), Independent("agent-2", "B"), Independent("agent-3", "C"), Independent("agent-4", "A") };

        var second = Tally.Decide(Choose(4, ["A", "B", "C"], ballots, TallyRound.FollowUp));

        Assert.Equal(TallyDecisionKind.Escalated, second.Kind);
        Assert.Equal(EscalationReason.NoDecisiveResult, second.Reason);
    }

    // Bigger change

    [Fact]
    public void Bigger_change_passes_only_when_unanimous()
    {
        var ballots = new[] { Independent("agent-1", "yes"), Independent("agent-2", "yes"), Independent("agent-3", "yes"), Independent("agent-4", "yes") };

        var result = Tally.Decide(Approve(4, ballots, motion: MotionClass.BiggerChange));

        Assert.Equal(TallyDecisionKind.Decided, result.Kind);
        Assert.Equal("yes", result.Choice);
    }

    [Fact]
    public void Bigger_change_with_a_dissent_is_not_decided_by_majority()
    {
        var ballots = new[] { Independent("agent-1", "yes"), Independent("agent-2", "yes"), Independent("agent-3", "yes"), Independent("agent-4", "no") };

        var result = Tally.Decide(Approve(4, ballots, motion: MotionClass.BiggerChange));

        Assert.Equal(TallyDecisionKind.FollowUpRequired, result.Kind);
    }

    [Fact]
    public void Bigger_change_with_an_abstention_is_not_unanimous()
    {
        var ballots = new[] { Independent("agent-1", "yes"), Independent("agent-2", "yes"), Independent("agent-3", "yes"), Independent("agent-4", "abstain") };

        var result = Tally.Decide(Approve(4, ballots, motion: MotionClass.BiggerChange));

        Assert.Equal(TallyDecisionKind.FollowUpRequired, result.Kind);
    }

    [Fact]
    public void Bigger_change_with_an_eligible_voter_who_never_cast_is_not_unanimous()
    {
        var ballots = new[] { Independent("agent-1", "yes"), Independent("agent-2", "yes"), Independent("agent-3", "yes") };

        var result = Tally.Decide(Approve(4, ballots, motion: MotionClass.BiggerChange));

        Assert.Equal(TallyDecisionKind.FollowUpRequired, result.Kind);
    }

    [Fact]
    public void Bigger_change_that_becomes_unanimous_after_follow_up_passes()
    {
        var ballots = new[] { Independent("agent-1", "yes"), Independent("agent-2", "yes"), Independent("agent-3", "no"), FollowUp("agent-3", "yes") };

        var result = Tally.Decide(Approve(3, ballots, TallyRound.FollowUp, MotionClass.BiggerChange));

        Assert.Equal(TallyDecisionKind.Decided, result.Kind);
        Assert.Equal("yes", result.Choice);
    }

    [Fact]
    public void Bigger_change_that_is_still_split_after_follow_up_escalates()
    {
        var ballots = new[] { Independent("agent-1", "yes"), Independent("agent-2", "yes"), Independent("agent-3", "no") };

        var result = Tally.Decide(Approve(3, ballots, TallyRound.FollowUp, MotionClass.BiggerChange));

        Assert.Equal(TallyDecisionKind.Escalated, result.Kind);
        Assert.Equal(EscalationReason.NotUnanimous, result.Reason);
    }

    [Fact]
    public void Unanimous_rejection_of_a_bigger_change_is_decided_as_no()
    {
        var ballots = new[] { Independent("agent-1", "no"), Independent("agent-2", "no"), Independent("agent-3", "no") };

        var result = Tally.Decide(Approve(3, ballots, motion: MotionClass.BiggerChange));

        Assert.Equal(TallyDecisionKind.Decided, result.Kind);
        Assert.Equal("no", result.Choice);
    }
}
