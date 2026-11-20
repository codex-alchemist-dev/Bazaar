using Bazaar.Server.Council.Service;

namespace Bazaar.Server.Council.Pilot;

/// <summary>Where a flipped choice moved, relative to the reveal's top choice.</summary>
public enum FlipDirection
{
    /// <summary>The follow-up choice is the reveal's top choice and the independent choice was not.</summary>
    TowardMajorityOfReveal,

    /// <summary>The independent choice was the reveal's top choice and the follow-up choice is not.</summary>
    Away,

    /// <summary>Neither choice is the top choice (or there is no single top choice).</summary>
    Other,
}

/// <summary>One voter whose choice changed between the independent ballot and the follow-up ballot.</summary>
/// <param name="VoterId">The voter.</param>
/// <param name="IndependentChoice">The sealed first choice.</param>
/// <param name="FollowUpChoice">The choice after the reveal.</param>
/// <param name="Direction">Direction relative to the reveal's top choice.</param>
/// <param name="EvidenceFree">True when the follow-up reasoning cites nothing from the reveal or subject (<see cref="EvidenceCitation"/> proxy).</param>
public sealed record Flip(
    string VoterId,
    string IndependentChoice,
    string FollowUpChoice,
    FlipDirection Direction,
    bool EvidenceFree);

/// <summary>Flip classification for one vote.</summary>
/// <param name="RevealTopChoice">The independent choice with the most ballots, or null when there is none or it is tied.</param>
/// <param name="FollowUpVoterCount">Voters who had both an independent and a follow-up ballot (the flip-rate denominator).</param>
/// <param name="Flips">Voters whose choice changed.</param>
public sealed record FlipAnalysis(string? RevealTopChoice, int FollowUpVoterCount, IReadOnlyList<Flip> Flips)
{
    /// <summary>Flips whose follow-up reasoning cites nothing from the reveal.</summary>
    public int EvidenceFreeFlipCount => Flips.Count(flip => flip.EvidenceFree);
}

/// <summary>
/// Pure flip classifier for doc 10 (Pilot metrics). No storage, no model calls. The reveal's top choice is the plurality
/// of independent ballots; abstentions (<see cref="ApprovalChoices.Abstain"/>) are not counted toward it, and a tie for
/// the top means there is no top choice. Evidence-free is decided by the <see cref="EvidenceCitation"/> proxy.
/// </summary>
public static class FlipClassifier
{
    /// <summary>The independent choice with the most ballots (abstentions excluded), or null when none or tied.</summary>
    public static string? RevealTopChoice(IEnumerable<StoredBallot> ballots)
    {
        ArgumentNullException.ThrowIfNull(ballots);

        var counts = ballots
            .Where(ballot => ballot.Phase == BallotPhase.Independent && ballot.Choice != ApprovalChoices.Abstain)
            .GroupBy(ballot => ballot.Choice, StringComparer.Ordinal)
            .Select(group => (Choice: group.Key, Count: group.Count()))
            .ToList();

        if (counts.Count == 0)
        {
            return null;
        }

        var max = counts.Max(count => count.Count);
        var leaders = counts.Where(count => count.Count == max).ToList();
        return leaders.Count == 1 ? leaders[0].Choice : null;
    }

    /// <summary>Classifies the flips of one vote.</summary>
    /// <param name="ballots">Both phases of one vote's ballots. At most one ballot per voter and phase.</param>
    /// <param name="subjectText">Optional subject text; its tokens also count as citable evidence.</param>
    /// <exception cref="ArgumentException">A voter has two ballots in the same phase.</exception>
    public static FlipAnalysis Classify(IReadOnlyList<StoredBallot> ballots, string? subjectText = null)
    {
        ArgumentNullException.ThrowIfNull(ballots);

        var independent = new Dictionary<string, StoredBallot>(StringComparer.Ordinal);
        var followUps = new Dictionary<string, StoredBallot>(StringComparer.Ordinal);
        foreach (var ballot in ballots)
        {
            var target = ballot.Phase == BallotPhase.Independent ? independent : followUps;
            if (!target.TryAdd(ballot.VoterId, ballot))
            {
                throw new ArgumentException($"Voter '{ballot.VoterId}' has more than one {ballot.Phase} ballot.", nameof(ballots));
            }
        }

        var top = RevealTopChoice(ballots);

        var citable = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ballot in independent.Values)
        {
            citable.UnionWith(EvidenceCitation.Extract(ballot.Reasoning));
        }

        citable.UnionWith(EvidenceCitation.Extract(subjectText));

        var flips = new List<Flip>();
        var followUpVoters = 0;
        foreach (var followUp in followUps.Values)
        {
            if (!independent.TryGetValue(followUp.VoterId, out var first))
            {
                continue;
            }

            followUpVoters++;
            if (string.Equals(first.Choice, followUp.Choice, StringComparison.Ordinal))
            {
                continue;
            }

            var direction = DirectionOf(top, first.Choice, followUp.Choice);
            var evidenceFree = !EvidenceCitation.IsEvidenceBased(followUp.Reasoning, citable);
            flips.Add(new Flip(followUp.VoterId, first.Choice, followUp.Choice, direction, evidenceFree));
        }

        return new FlipAnalysis(top, followUpVoters, flips);
    }

    private static FlipDirection DirectionOf(string? top, string independentChoice, string followUpChoice)
    {
        if (top is null)
        {
            return FlipDirection.Other;
        }

        if (followUpChoice == top && independentChoice != top)
        {
            return FlipDirection.TowardMajorityOfReveal;
        }

        if (independentChoice == top && followUpChoice != top)
        {
            return FlipDirection.Away;
        }

        return FlipDirection.Other;
    }
}
