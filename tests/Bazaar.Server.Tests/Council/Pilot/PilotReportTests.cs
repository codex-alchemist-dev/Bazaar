using Bazaar.Server.Council;
using Bazaar.Server.Council.Pilot;
using Bazaar.Server.Council.Service;

namespace Bazaar.Server.Tests.Council.Pilot;

public class PilotReportTests
{
    private const string _evidenceFreeReason = "Everyone seems right, going along.";
    private const string _evidenceBasedReason = "Rollback steps are present, so yes.";
    private const string _stillNoReason = "Still not convinced.";

    private static readonly PilotSettings _settings = new();

    private static StoredBallot Ballot(string voter, BallotPhase phase, string choice, string reason) =>
        new(Guid.Empty, voter, phase, choice, reason, false, DateTimeOffset.UnixEpoch);

    /// <summary>
    /// Independent top is yes (a, b say yes; c says no). When <paramref name="followUpReason"/> is set, c follows up with
    /// <paramref name="followUpChoice"/>. The tally and outcome fields are passed through.
    /// </summary>
    private static PilotVote Vote(
        string? followUpReason,
        string followUpChoice = ApprovalChoices.Yes,
        string? tally = ApprovalChoices.Yes,
        OutcomeLabel? outcome = null,
        string? correct = null)
    {
        var ballots = new List<StoredBallot>
        {
            Ballot("a", BallotPhase.Independent, ApprovalChoices.Yes, "Rollback steps are present."),
            Ballot("b", BallotPhase.Independent, ApprovalChoices.Yes, "Tests look fine."),
            Ballot("c", BallotPhase.Independent, ApprovalChoices.No, "Undecided."),
        };
        if (followUpReason is not null)
        {
            ballots.Add(Ballot("c", BallotPhase.FollowUp, followUpChoice, followUpReason));
        }

        return new PilotVote(Guid.NewGuid(), ballots, tally, null, outcome, correct);
    }

    private static PilotReport Report(IReadOnlyList<PilotVote> votes, PilotSettings? settings = null) =>
        PilotReport.Build(votes, new Dictionary<string, string>(), settings ?? _settings);

    [Fact]
    public void Stop_rule_is_off_below_the_target_vote_count()
    {
        var votes = Enumerable.Range(0, 19).Select(_ => Vote(_evidenceFreeReason)).ToList();

        var report = Report(votes);

        Assert.Equal(PilotStopOutcome.InsufficientData, report.StopRule.Outcome);
        Assert.Equal(19, report.VoteCount);
        Assert.Equal(19, report.EvidenceFreeFlipCount);
    }

    [Fact]
    public void Stop_rule_fires_when_evidence_free_share_exceeds_the_threshold_with_enough_votes()
    {
        var votes = Enumerable.Range(0, 20).Select(_ => Vote(_evidenceFreeReason)).ToList();

        var report = Report(votes);

        Assert.Equal(1.0, report.EvidenceFreeFlipShare);
        Assert.Equal(PilotStopOutcome.StopFollowUp, report.StopRule.Outcome);
        Assert.Contains(report.StopRule.Reasons, reason => reason.Contains("Evidence-free", StringComparison.Ordinal));
    }

    [Fact]
    public void Stop_rule_does_not_fire_when_evidence_free_share_is_at_or_under_the_threshold()
    {
        // Share 4 of 20 flips is exactly 0.20, which is not above 0.20.
        var atThreshold = Enumerable.Range(0, 4).Select(_ => Vote(_evidenceFreeReason))
            .Concat(Enumerable.Range(0, 16).Select(_ => Vote(_evidenceBasedReason)))
            .ToList();

        var report = Report(atThreshold);

        Assert.Equal(20, report.FlipCount);
        Assert.Equal(0.2, report.EvidenceFreeFlipShare);
        Assert.Equal(PilotStopOutcome.Continue, report.StopRule.Outcome);

        // 5 of 20 is 0.25, which is above the threshold.
        var aboveThreshold = Enumerable.Range(0, 5).Select(_ => Vote(_evidenceFreeReason))
            .Concat(Enumerable.Range(0, 15).Select(_ => Vote(_evidenceBasedReason)))
            .ToList();

        Assert.Equal(PilotStopOutcome.StopFollowUp, Report(aboveThreshold).StopRule.Outcome);
    }

    [Fact]
    public void Stop_rule_fires_when_final_accuracy_is_below_independent_accuracy()
    {
        // Independent top is yes and correct on all 20 votes. Five owner-decided or tallied "no" results are wrong.
        var votes = Enumerable.Range(0, 5).Select(_ => Vote(null, tally: ApprovalChoices.No, outcome: OutcomeLabel.Incorrect, correct: ApprovalChoices.Yes))
            .Concat(Enumerable.Range(0, 15).Select(_ => Vote(null, tally: ApprovalChoices.Yes, outcome: OutcomeLabel.Correct)))
            .ToList();

        var report = Report(votes);

        Assert.Equal(20, report.ComparableVoteCount);
        Assert.Equal(1.0, report.IndependentAccuracy);
        Assert.Equal(0.75, report.FinalAccuracy);
        Assert.Equal(0, report.FlipCount);
        Assert.Equal(PilotStopOutcome.StopFollowUp, report.StopRule.Outcome);
        Assert.Contains(report.StopRule.Reasons, reason => reason.Contains("below independent accuracy", StringComparison.Ordinal));
    }

    [Fact]
    public void Stop_rule_continues_when_accuracy_is_equal_and_flips_are_evidence_based()
    {
        var votes = Enumerable.Range(0, 20).Select(_ => Vote(_evidenceBasedReason, outcome: OutcomeLabel.Correct)).ToList();

        var report = Report(votes);

        Assert.Equal(PilotStopOutcome.Continue, report.StopRule.Outcome);
        Assert.Equal(report.IndependentAccuracy, report.FinalAccuracy);
    }

    [Fact]
    public void Flip_rates_and_direction_are_computed_over_follow_up_voters()
    {
        var votes = new[]
        {
            Vote(_evidenceFreeReason),
            Vote(_evidenceBasedReason),
            Vote(_stillNoReason, followUpChoice: ApprovalChoices.No),
        };

        var report = Report(votes);

        Assert.Equal(3, report.FollowUpVoterCount);
        Assert.Equal(2, report.FlipCount);
        Assert.Equal(2.0 / 3.0, report.FlipRate!.Value, precision: 10);
        Assert.Equal(2.0 / 3.0, report.FlipRateByDirection[FlipDirection.TowardMajorityOfReveal]!.Value, precision: 10);
        Assert.Equal(0.0, report.FlipRateByDirection[FlipDirection.Away]);
        Assert.Equal(1.0 / 3.0, report.EvidenceFlipRate!.Value, precision: 10);
        Assert.Equal(0.5, report.EvidenceFreeFlipShare);
    }

    [Fact]
    public void Rates_are_null_when_there_are_no_follow_up_voters()
    {
        var report = Report([Vote(null)]);

        Assert.Null(report.FlipRate);
        Assert.Null(report.EvidenceFreeFlipShare);
        Assert.Null(report.IndependentAccuracy);
    }

    [Fact]
    public void Agreement_by_family_is_computed_against_the_reveal_top_choice()
    {
        var families = new Dictionary<string, string>
        {
            ["a"] = "claude",
            ["b"] = "claude",
            ["c"] = "gpt",
        };
        var votes = new[]
        {
            new PilotVote(
                Guid.NewGuid(),
                [
                    Ballot("a", BallotPhase.Independent, ApprovalChoices.Yes, "Reason."),
                    Ballot("b", BallotPhase.Independent, ApprovalChoices.Yes, "Reason."),
                    Ballot("c", BallotPhase.Independent, ApprovalChoices.No, "Reason."),
                ],
                TallyChoice: ApprovalChoices.Yes,
                OwnerResolution: null,
                Outcome: null),
            new PilotVote(
                Guid.NewGuid(),
                [
                    Ballot("a", BallotPhase.Independent, ApprovalChoices.No, "Reason."),
                    Ballot("b", BallotPhase.Independent, ApprovalChoices.Yes, "Reason."),
                    Ballot("c", BallotPhase.Independent, ApprovalChoices.No, "Reason."),
                ],
                TallyChoice: ApprovalChoices.No,
                OwnerResolution: null,
                Outcome: null),
        };

        var report = PilotReport.Build(votes, families, _settings);

        Assert.Equal(new FamilyAgreement(3, 4), report.AgreementByFamily["claude"]);
        Assert.Equal(0.75, report.AgreementByFamily["claude"].Share);
        Assert.Equal(new FamilyAgreement(1, 2), report.AgreementByFamily["gpt"]);
        Assert.Equal(0.5, report.AgreementByFamily["gpt"].Share);
    }

    [Fact]
    public void Voters_missing_from_the_family_map_are_counted_as_unknown()
    {
        var report = Report([Vote(null)]);

        Assert.Equal(new FamilyAgreement(2, 3), report.AgreementByFamily[PilotReport.UnknownFamily]);
    }

    [Fact]
    public void Ties_escalations_overrides_and_tokens_are_counted()
    {
        var votes = new[]
        {
            new PilotVote(Guid.NewGuid(), [], TallyChoice: null, OwnerResolution: null, Outcome: null, Tied: true, Tokens: 100),
            new PilotVote(Guid.NewGuid(), [], TallyChoice: ApprovalChoices.Yes, OwnerResolution: ApprovalChoices.No, Outcome: null, Tokens: 300),
            new PilotVote(Guid.NewGuid(), [], TallyChoice: ApprovalChoices.Yes, OwnerResolution: ApprovalChoices.Yes, Outcome: null),
        };

        var report = PilotReport.Build(votes, new Dictionary<string, string>(), _settings);

        Assert.Equal(1, report.TieCount);
        Assert.Equal(1, report.EscalationCount);
        Assert.Equal(1, report.OverrideCount);
        Assert.Equal(200.0, report.TokensPerVote);
    }

    [Fact]
    public void Accuracy_uses_only_votes_with_a_known_correct_choice()
    {
        var votes = new[]
        {
            Vote(null, outcome: OutcomeLabel.Correct),
            Vote(null, outcome: OutcomeLabel.Incorrect),
            Vote(null, outcome: null),
        };

        var report = Report(votes);

        Assert.Equal(2, report.OutcomeVoteCount);
        Assert.Equal(1, report.ComparableVoteCount);
        Assert.Equal(1.0, report.FinalAccuracy);
    }
}
