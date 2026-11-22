namespace Bazaar.Server.Reputation;

/// <summary>
/// Pure, deterministic reputation scorer. No database, no clock: the caller passes <c>now</c>.
/// Evidence is sorted into a total order before summing, so the result does not depend on input order.
/// </summary>
public static class ScoreMath
{
    public static ScoreResult Compute(IEnumerable<Evidence> evidence, WeightPolicy policy, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(policy);
        Validate(policy);

        // One-sided 95% z value for the lower confidence bound.
        const double lcbZ = 1.645;

        // Floating-point addition is not associative, so sum in a fixed order.
        var ordered = evidence
            .OrderBy(e => e.OccurredAt)
            .ThenBy(e => e.Kind)
            .ThenBy(e => e.X)
            .ThenBy(e => e.ReviewerReliability)
            .ToList();

        double sumW = 0;
        double sumWx = 0;
        foreach (var item in ordered)
        {
            var ageDays = Math.Max(0, (now - item.OccurredAt).TotalDays);
            var w = BaseWeight(item, policy) * Math.Pow(0.5, ageDays / policy.HalfLifeDays);
            sumW += w;
            sumWx += w * item.X;
        }

        var k = policy.PriorK;
        var denom = sumW + k;

        // Only reachable with no weighted evidence and PriorK = 0: there is nothing to score, so report the neutral point.
        if (denom == 0)
        {
            return new ScoreResult(0.5, 0, 0, policy.Version);
        }

        var s = (sumWx + 0.5 * k) / denom;
        var lcb = s - lcbZ * Math.Sqrt(s * (1 - s) / denom);

        // NEff is the decayed evidence mass without the prior. This is an assumption: doc 11 defines n_eff as Σw.
        return new ScoreResult(s, lcb, sumW, policy.Version);
    }

    private static double BaseWeight(Evidence item, WeightPolicy policy) => item.Kind switch
    {
        EvidenceKind.OwnerAccept or EvidenceKind.OwnerReject or EvidenceKind.Manual => policy.OwnerOutcome,
        EvidenceKind.Revert => policy.Revert,
        EvidenceKind.PostMergeDefect => policy.PostMergeDefect,
        EvidenceKind.CiFirstRun => policy.CiFirstRun,
        EvidenceKind.PeerReview => policy.PeerReview * item.ReviewerReliability,
        _ => throw new ArgumentOutOfRangeException(nameof(item), item.Kind, "Unknown evidence kind."),
    };

    private static void Validate(WeightPolicy policy)
    {
        RequireNonNegative(policy.OwnerOutcome, nameof(policy.OwnerOutcome));
        RequireNonNegative(policy.Revert, nameof(policy.Revert));
        RequireNonNegative(policy.PostMergeDefect, nameof(policy.PostMergeDefect));
        RequireNonNegative(policy.CiFirstRun, nameof(policy.CiFirstRun));
        RequireNonNegative(policy.PeerReview, nameof(policy.PeerReview));
        RequireNonNegative(policy.PriorK, nameof(policy.PriorK));

        if (!(double.IsFinite(policy.HalfLifeDays) && policy.HalfLifeDays > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(policy), policy.HalfLifeDays, "HalfLifeDays must be a positive finite number of days.");
        }
    }

    private static void RequireNonNegative(double value, string name)
    {
        if (!(double.IsFinite(value) && value >= 0))
        {
            throw new ArgumentOutOfRangeException(name, value, "Weight must be a finite number >= 0.");
        }
    }
}
