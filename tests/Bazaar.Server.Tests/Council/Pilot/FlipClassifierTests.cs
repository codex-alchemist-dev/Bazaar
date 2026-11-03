using Bazaar.Server.Council;
using Bazaar.Server.Council.Pilot;
using Bazaar.Server.Council.Service;

namespace Bazaar.Server.Tests.Council.Pilot;

public class FlipClassifierTests
{
    private static readonly Guid _voteId = Guid.Parse("00000000-0000-0000-0000-000000000008");

    private static StoredBallot Independent(string voter, string choice, string reason) =>
        new(_voteId, voter, BallotPhase.Independent, choice, reason, false, DateTimeOffset.UnixEpoch);

    private static StoredBallot FollowUp(string voter, string choice, string reason) =>
        new(_voteId, voter, BallotPhase.FollowUp, choice, reason, false, DateTimeOffset.UnixEpoch);

    [Fact]
    public void Flip_toward_the_reveal_majority_is_classified()
    {
        var ballots = new[]
        {
            Independent("a", ApprovalChoices.Yes, "The rollback steps are missing."),
            Independent("b", ApprovalChoices.Yes, "Tests cover the change."),
            Independent("c", ApprovalChoices.No, "Unsure about the rollback."),
            FollowUp("c", ApprovalChoices.Yes, "The rollback steps are missing and tests cover it."),
        };

        var analysis = FlipClassifier.Classify(ballots);

        Assert.Equal(ApprovalChoices.Yes, analysis.RevealTopChoice);
        Assert.Equal(1, analysis.FollowUpVoterCount);
        var flip = Assert.Single(analysis.Flips);
        Assert.Equal("c", flip.VoterId);
        Assert.Equal(FlipDirection.TowardMajorityOfReveal, flip.Direction);
        Assert.Equal(ApprovalChoices.No, flip.IndependentChoice);
        Assert.Equal(ApprovalChoices.Yes, flip.FollowUpChoice);
    }

    [Fact]
    public void Flip_away_from_the_reveal_majority_is_classified()
    {
        var ballots = new[]
        {
            Independent("a", ApprovalChoices.Yes, "Looks good."),
            Independent("b", ApprovalChoices.Yes, "Looks good too."),
            Independent("c", ApprovalChoices.No, "Risky."),
            FollowUp("a", ApprovalChoices.No, "Persuaded by the risk argument."),
        };

        var flip = Assert.Single(FlipClassifier.Classify(ballots).Flips);

        Assert.Equal(FlipDirection.Away, flip.Direction);
    }

    [Fact]
    public void Flip_between_two_non_top_choices_is_other()
    {
        var ballots = new[]
        {
            Independent("a", "alpha", "First reason."),
            Independent("b", "alpha", "Second reason."),
            Independent("c", "beta", "Third reason."),
            Independent("d", "gamma", "Fourth reason."),
            FollowUp("d", "beta", "Changing to beta for a different reason."),
        };

        var flip = Assert.Single(FlipClassifier.Classify(ballots).Flips);

        Assert.Equal(FlipDirection.Other, flip.Direction);
    }

    [Fact]
    public void Tied_reveal_has_no_top_choice_so_every_flip_is_other()
    {
        var ballots = new[]
        {
            Independent("a", ApprovalChoices.Yes, "Reason."),
            Independent("b", ApprovalChoices.No, "Reason."),
            FollowUp("b", ApprovalChoices.Yes, "Reason."),
        };

        var analysis = FlipClassifier.Classify(ballots);

        Assert.Null(analysis.RevealTopChoice);
        Assert.Equal(FlipDirection.Other, Assert.Single(analysis.Flips).Direction);
    }

    [Fact]
    public void Abstentions_do_not_count_toward_the_top_choice()
    {
        var ballots = new[]
        {
            Independent("a", ApprovalChoices.Abstain, "Not sure."),
            Independent("b", ApprovalChoices.Abstain, "Not sure either."),
            Independent("c", ApprovalChoices.No, "Reject."),
        };

        Assert.Equal(ApprovalChoices.No, FlipClassifier.RevealTopChoice(ballots));
    }

    [Fact]
    public void Follow_up_that_cites_nothing_from_the_reveal_is_evidence_free()
    {
        var ballots = new[]
        {
            Independent("a", ApprovalChoices.Yes, "The migration plan lacks rollback steps."),
            Independent("b", ApprovalChoices.Yes, "Migration is fine."),
            Independent("c", ApprovalChoices.No, "Hesitant overall."),
            FollowUp("c", ApprovalChoices.Yes, "Everyone seems right, so I go along with them."),
        };

        var flip = Assert.Single(FlipClassifier.Classify(ballots).Flips);

        Assert.True(flip.EvidenceFree);
    }

    [Fact]
    public void Follow_up_that_cites_a_revealed_token_is_not_evidence_free()
    {
        var ballots = new[]
        {
            Independent("a", ApprovalChoices.Yes, "Tests pass on PR #123 and the rollout is safe."),
            Independent("b", ApprovalChoices.Yes, "Agree on the design."),
            Independent("c", ApprovalChoices.No, "Not convinced yet."),
            FollowUp("c", ApprovalChoices.Yes, "PR #123 has passing tests, so I changed my vote."),
        };

        var analysis = FlipClassifier.Classify(ballots);

        var flip = Assert.Single(analysis.Flips);
        Assert.False(flip.EvidenceFree);
        Assert.Equal(0, analysis.EvidenceFreeFlipCount);
    }

    [Fact]
    public void Subject_text_counts_as_citable_evidence()
    {
        var ballots = new[]
        {
            Independent("a", ApprovalChoices.Yes, "Yes."),
            Independent("b", ApprovalChoices.No, "No."),
            FollowUp("b", ApprovalChoices.Yes, "The Hiring Rubric settles it."),
        };

        Assert.True(FlipClassifier.Classify(ballots).Flips[0].EvidenceFree);
        Assert.False(FlipClassifier.Classify(ballots, "Review the Hiring Rubric draft.").Flips[0].EvidenceFree);
    }

    [Fact]
    public void Unchanged_follow_up_is_counted_as_a_follow_up_voter_but_not_a_flip()
    {
        var ballots = new[]
        {
            Independent("a", ApprovalChoices.Yes, "Reason."),
            Independent("b", ApprovalChoices.No, "Reason."),
            FollowUp("a", ApprovalChoices.Yes, "Still yes."),
        };

        var analysis = FlipClassifier.Classify(ballots);

        Assert.Equal(1, analysis.FollowUpVoterCount);
        Assert.Empty(analysis.Flips);
    }

    [Fact]
    public void Duplicate_ballot_in_one_phase_is_rejected()
    {
        var ballots = new[]
        {
            Independent("a", ApprovalChoices.Yes, "One."),
            Independent("a", ApprovalChoices.No, "Two."),
        };

        Assert.Throws<ArgumentException>(() => FlipClassifier.Classify(ballots));
    }
}
