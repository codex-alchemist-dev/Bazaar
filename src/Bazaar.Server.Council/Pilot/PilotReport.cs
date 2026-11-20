using Bazaar.Server.Council.Service;

namespace Bazaar.Server.Council.Pilot;

/// <summary>Agreement with the reveal's top choice for one model family.</summary>
/// <param name="Agreeing">Independent ballots that matched the reveal's top choice.</param>
/// <param name="Ballots">Independent ballots counted (votes with a top choice only).</param>
public sealed record FamilyAgreement(int Agreeing, int Ballots)
{
    /// <summary>Agreeing over ballots, or null when there are no ballots.</summary>
    public double? Share => Ballots == 0 ? null : (double)Agreeing / Ballots;
}

/// <summary>What the stop rule decided.</summary>
public enum PilotStopOutcome
{
    /// <summary>Fewer votes than council.pilot_target_votes. The rule was not evaluated and does not stop.</summary>
    InsufficientData,

    /// <summary>Enough votes, and neither condition holds. Follow-up continues.</summary>
    Continue,

    /// <summary>Follow-up should be turned off.</summary>
    StopFollowUp,
}

/// <summary>The stop rule's decision and the reasons behind it.</summary>
/// <param name="Outcome">Decision.</param>
/// <param name="Reasons">Human-readable reasons, one per condition that held or a single line when data is insufficient.</param>
public sealed record PilotStopDecision(PilotStopOutcome Outcome, IReadOnlyList<string> Reasons);

/// <summary>Aggregate pilot metrics (doc 10, Pilot metrics). Pure: no storage, no clock, no model calls.</summary>
/// <param name="VoteCount">Votes in the report.</param>
/// <param name="FollowUpVoterCount">Voters with both an independent and a follow-up ballot.</param>
/// <param name="FlipCount">Voters whose choice changed.</param>
/// <param name="FlipRate">Flips over follow-up voters, or null when there are none.</param>
/// <param name="FlipRateByDirection">Flips per direction over follow-up voters; all directions present, null when no follow-up voters.</param>
/// <param name="EvidenceFreeFlipCount">Flips whose follow-up reasoning cites nothing from the reveal (proxy).</param>
/// <param name="EvidenceFlipRate">Evidence-free flips over follow-up voters.</param>
/// <param name="EvidenceFreeFlipShare">Evidence-free flips over flips. The stop rule uses this. Null when there are no flips.</param>
/// <param name="OutcomeVoteCount">Votes with an outcome label.</param>
/// <param name="ComparableVoteCount">Outcome votes with a known correct choice; accuracy is computed over these.</param>
/// <param name="IndependentAccuracy">Reveal top choice equals the correct choice, over comparable votes.</param>
/// <param name="FinalAccuracy">Final choice equals the correct choice, over comparable votes.</param>
/// <param name="TieCount">Votes that ended in a tie.</param>
/// <param name="EscalationCount">Votes the tally escalated (no decisive choice).</param>
/// <param name="OverrideCount">Votes where the owner's resolution differed from the tally's choice.</param>
/// <param name="AgreementByFamily">Agreement with the reveal top choice, per model family.</param>
/// <param name="TokensPerVote">Mean tokens over votes that reported tokens, or null when none did.</param>
/// <param name="StopRule">Stop-rule decision.</param>
public sealed record PilotReport(
    int VoteCount,
    int FollowUpVoterCount,
    int FlipCount,
    double? FlipRate,
    IReadOnlyDictionary<FlipDirection, double?> FlipRateByDirection,
    int EvidenceFreeFlipCount,
    double? EvidenceFlipRate,
    double? EvidenceFreeFlipShare,
    int OutcomeVoteCount,
    int ComparableVoteCount,
    double? IndependentAccuracy,
    double? FinalAccuracy,
    int TieCount,
    int EscalationCount,
    int OverrideCount,
    IReadOnlyDictionary<string, FamilyAgreement> AgreementByFamily,
    double? TokensPerVote,
    PilotStopDecision StopRule)
{
    /// <summary>Family name used for voters missing from the family map.</summary>
    public const string UnknownFamily = "unknown";

    /// <summary>
    /// Builds the report. Settings are validated first; invalid settings throw.
    /// The stop rule needs at least <see cref="PilotSettings.TargetVotes"/> votes. Above that it stops when the
    /// evidence-free flip share is above <see cref="PilotSettings.StopFlipShare"/>, or when final accuracy is below
    /// independent accuracy over comparable votes.
    /// </summary>
    /// <param name="votes">Closed votes in the pilot.</param>
    /// <param name="familyByVoter">Voter id to model family. Voters missing here count as <see cref="UnknownFamily"/>.</param>
    /// <param name="settings">Pilot settings.</param>
    /// <exception cref="ArgumentException">The settings are invalid.</exception>
    public static PilotReport Build(
        IReadOnlyList<PilotVote> votes,
        IReadOnlyDictionary<string, string> familyByVoter,
        PilotSettings settings)
    {
        ArgumentNullException.ThrowIfNull(votes);
        ArgumentNullException.ThrowIfNull(familyByVoter);
        ArgumentNullException.ThrowIfNull(settings);

        var errors = settings.Validate();
        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", errors), nameof(settings));
        }

        var followUpVoters = 0;
        var flips = new List<Flip>();
        var ties = 0;
        var escalations = 0;
        var overrides = 0;
        var outcomeVotes = 0;
        var comparable = 0;
        var independentCorrect = 0;
        var finalCorrect = 0;
        var tokenTotal = 0L;
        var tokenVotes = 0;
        var agreement = new Dictionary<string, (int Agreeing, int Ballots)>(StringComparer.Ordinal);

        foreach (var vote in votes)
        {
            var analysis = FlipClassifier.Classify(vote.Ballots, vote.SubjectText);
            followUpVoters += analysis.FollowUpVoterCount;
            flips.AddRange(analysis.Flips);

            if (vote.Tied)
            {
                ties++;
            }

            if (vote.Escalated)
            {
                escalations++;
            }

            if (vote.OwnerResolution is not null && vote.TallyChoice is not null && vote.OwnerResolution != vote.TallyChoice)
            {
                overrides++;
            }

            if (vote.Tokens is { } tokens)
            {
                tokenTotal += tokens;
                tokenVotes++;
            }

            AddAgreement(agreement, vote.Ballots, analysis.RevealTopChoice, familyByVoter);

            if (vote.Outcome is null)
            {
                continue;
            }

            outcomeVotes++;
            var correct = vote.CorrectChoice
                ?? (vote.Outcome == OutcomeLabel.Correct ? vote.FinalChoice : null);
            if (correct is null)
            {
                continue;
            }

            comparable++;
            if (analysis.RevealTopChoice == correct)
            {
                independentCorrect++;
            }

            if (vote.FinalChoice == correct)
            {
                finalCorrect++;
            }
        }

        var evidenceFree = flips.Count(flip => flip.EvidenceFree);
        var flipCount = flips.Count;

        var byDirection = Enum.GetValues<FlipDirection>().ToDictionary(
            direction => direction,
            direction => Rate(flips.Count(flip => flip.Direction == direction), followUpVoters));

        var familyAgreement = agreement.ToDictionary(
            pair => pair.Key,
            pair => new FamilyAgreement(pair.Value.Agreeing, pair.Value.Ballots),
            StringComparer.Ordinal);

        var independentAccuracy = Rate(independentCorrect, comparable);
        var finalAccuracy = Rate(finalCorrect, comparable);
        var stop = DecideStop(votes.Count, evidenceFree, flipCount, independentAccuracy, finalAccuracy, settings);

        return new PilotReport(
            VoteCount: votes.Count,
            FollowUpVoterCount: followUpVoters,
            FlipCount: flipCount,
            FlipRate: Rate(flipCount, followUpVoters),
            FlipRateByDirection: byDirection,
            EvidenceFreeFlipCount: evidenceFree,
            EvidenceFlipRate: Rate(evidenceFree, followUpVoters),
            EvidenceFreeFlipShare: Rate(evidenceFree, flipCount),
            OutcomeVoteCount: outcomeVotes,
            ComparableVoteCount: comparable,
            IndependentAccuracy: independentAccuracy,
            FinalAccuracy: finalAccuracy,
            TieCount: ties,
            EscalationCount: escalations,
            OverrideCount: overrides,
            AgreementByFamily: familyAgreement,
            TokensPerVote: tokenVotes == 0 ? null : (double)tokenTotal / tokenVotes,
            StopRule: stop);
    }

    private static void AddAgreement(
        Dictionary<string, (int Agreeing, int Ballots)> agreement,
        IReadOnlyList<StoredBallot> ballots,
        string? top,
        IReadOnlyDictionary<string, string> familyByVoter)
    {
        if (top is null)
        {
            return;
        }

        foreach (var ballot in ballots.Where(ballot => ballot.Phase == BallotPhase.Independent))
        {
            var family = familyByVoter.GetValueOrDefault(ballot.VoterId, UnknownFamily);
            var (agreeing, total) = agreement.GetValueOrDefault(family);
            agreement[family] = (agreeing + (ballot.Choice == top ? 1 : 0), total + 1);
        }
    }

    private static PilotStopDecision DecideStop(
        int voteCount,
        int evidenceFree,
        int flipCount,
        double? independentAccuracy,
        double? finalAccuracy,
        PilotSettings settings)
    {
        if (voteCount < settings.TargetVotes)
        {
            return new PilotStopDecision(
                PilotStopOutcome.InsufficientData,
                [$"Insufficient data: {voteCount} of {settings.TargetVotes} target votes; stop rule not evaluated."]);
        }

        var reasons = new List<string>();
        var evidenceFreeShare = Rate(evidenceFree, flipCount);
        if (evidenceFreeShare is { } share && share > (double)settings.StopFlipShare)
        {
            reasons.Add(
                $"Evidence-free flip share {share:P0} exceeds council.pilot_stop_flip_share {settings.StopFlipShare:P0}.");
        }

        if (independentAccuracy is { } independent && finalAccuracy is { } final && final < independent)
        {
            reasons.Add(
                $"Final accuracy {final:P0} is below independent accuracy {independent:P0}.");
        }

        return reasons.Count > 0
            ? new PilotStopDecision(PilotStopOutcome.StopFollowUp, reasons)
            : new PilotStopDecision(PilotStopOutcome.Continue, ["No stop condition holds."]);
    }

    private static double? Rate(int numerator, int denominator) =>
        denominator == 0 ? null : (double)numerator / denominator;
}
