using Bazaar.Server.Council;

namespace Bazaar.Server.Tests.Council;

public class TallyValidationTests
{
    private static HashSet<string> Electorate(int count) =>
        Enumerable.Range(1, count).Select(i => $"agent-{i}").ToHashSet();

    private static TallyRequest Approve(IReadOnlyList<Ballot> ballots, TallyRound round = TallyRound.Independent, int quorumMin = 1) =>
        new(VoteKind.ApproveReject, MotionClass.Ordinary, round, Electorate(3), ballots, [], quorumMin);

    [Fact]
    public void Null_request_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => Tally.Decide(null!));
    }

    [Fact]
    public void Ballot_from_a_non_eligible_voter_is_rejected()
    {
        var ballots = new[] { new Ballot("stranger", "yes", "r", BallotPhase.Independent) };

        Assert.Throws<ArgumentException>(() => Tally.Decide(Approve(ballots)));
    }

    [Fact]
    public void Two_independent_ballots_from_one_voter_are_rejected()
    {
        var ballots = new[]
        {
            new Ballot("agent-1", "yes", "r", BallotPhase.Independent),
            new Ballot("agent-1", "no", "r", BallotPhase.Independent),
        };

        Assert.Throws<ArgumentException>(() => Tally.Decide(Approve(ballots)));
    }

    [Fact]
    public void Two_follow_up_ballots_from_one_voter_are_rejected()
    {
        var ballots = new[]
        {
            new Ballot("agent-1", "yes", "r", BallotPhase.Independent),
            new Ballot("agent-1", "no", "r", BallotPhase.FollowUp),
            new Ballot("agent-1", "yes", "r", BallotPhase.FollowUp),
        };

        Assert.Throws<ArgumentException>(() => Tally.Decide(Approve(ballots, TallyRound.FollowUp)));
    }

    [Fact]
    public void Follow_up_ballot_is_rejected_in_the_independent_round()
    {
        var ballots = new[] { new Ballot("agent-1", "yes", "r", BallotPhase.FollowUp) };

        Assert.Throws<ArgumentException>(() => Tally.Decide(Approve(ballots)));
    }

    [Fact]
    public void Approve_reject_choice_outside_the_fixed_set_is_rejected()
    {
        var ballots = new[] { new Ballot("agent-1", "maybe", "r", BallotPhase.Independent) };

        Assert.Throws<ArgumentException>(() => Tally.Decide(Approve(ballots)));
    }

    [Fact]
    public void Approve_reject_takes_no_options()
    {
        var request = new TallyRequest(VoteKind.ApproveReject, MotionClass.Ordinary, TallyRound.Independent, Electorate(3), [], ["A"]);

        Assert.Throws<ArgumentException>(() => Tally.Decide(request));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    public void Option_choice_needs_two_to_six_options(int count)
    {
        var options = Enumerable.Range(1, count).Select(i => $"opt-{i}").ToList();
        var request = new TallyRequest(VoteKind.OptionChoice, MotionClass.Ordinary, TallyRound.Independent, Electorate(3), [], options);

        Assert.Throws<ArgumentException>(() => Tally.Decide(request));
    }

    [Fact]
    public void Option_choice_options_must_be_distinct()
    {
        var request = new TallyRequest(VoteKind.OptionChoice, MotionClass.Ordinary, TallyRound.Independent, Electorate(3), [], ["A", "A"]);

        Assert.Throws<ArgumentException>(() => Tally.Decide(request));
    }

    [Fact]
    public void Option_choice_ballot_must_name_a_listed_option()
    {
        var request = new TallyRequest(
            VoteKind.OptionChoice,
            MotionClass.Ordinary,
            TallyRound.Independent,
            Electorate(3),
            [new Ballot("agent-1", "Z", "r", BallotPhase.Independent)],
            ["A", "B"]);

        Assert.Throws<ArgumentException>(() => Tally.Decide(request));
    }

    [Fact]
    public void Quorum_minimum_below_one_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => Tally.Decide(Approve([], quorumMin: 0)));
    }
}
