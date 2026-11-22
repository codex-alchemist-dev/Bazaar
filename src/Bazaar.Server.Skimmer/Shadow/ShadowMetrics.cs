using Bazaar.Server.Skimmer.Rules;

namespace Bazaar.Server.Skimmer.Shadow;

/// <summary>A shadow decision with its label: whether the agent should have been woken for the message.</summary>
/// <param name="Decision">The recorded shadow decision.</param>
/// <param name="ShouldWake">The label from owner review or outcomes (doc 13, "Measurement").</param>
public sealed record LabelledDecision(Bazaar.Server.Skimmer.Digest.GateDecision Decision, bool ShouldWake);

/// <summary>
/// The shadow metrics view (doc 13, "Measurement"). Rates are basis points, or null when the denominator is zero.
/// </summary>
/// <param name="Labelled">Labelled decisions counted.</param>
/// <param name="WouldPromote">Scored-path wakes that shadow mode would have promoted.</param>
/// <param name="TruePromotions">Would-promote decisions whose label says the agent should wake.</param>
/// <param name="FalsePromotions">Would-promote decisions whose label says it should not (false wakes).</param>
/// <param name="Misses">Deferred decisions whose label says the agent should have woken.</param>
/// <param name="PrecisionBasisPoints">TruePromotions over WouldPromote.</param>
/// <param name="MissRateBasisPoints">Misses over every labelled message that should wake.</param>
/// <param name="MissUpperBoundBasisPoints">The 95% Wilson upper bound of the miss rate.</param>
/// <param name="ReadyForPromotion">True when enough labels exist and the miss upper bound is within the limit. Informational: the owner approves any change of mode.</param>
public sealed record ShadowMetrics(
    int Labelled,
    int WouldPromote,
    int TruePromotions,
    int FalsePromotions,
    int Misses,
    int? PrecisionBasisPoints,
    int? MissRateBasisPoints,
    int? MissUpperBoundBasisPoints,
    bool ReadyForPromotion)
{
    /// <summary>Computes the metrics. Only shadow records count; non-shadow records are ignored.</summary>
    /// <param name="labelled">The labelled decisions.</param>
    /// <param name="minLabels"><c>skimmer.promotion_min_labels</c>.</param>
    /// <param name="maxMissBoundBasisPoints"><c>skimmer.promotion_max_miss_bound_bp</c>.</param>
    public static ShadowMetrics Compute(IEnumerable<LabelledDecision> labelled, int minLabels = 150, int maxMissBoundBasisPoints = 200)
    {
        ArgumentNullException.ThrowIfNull(labelled);
        int total = 0, promote = 0, truePromote = 0, miss = 0, positives = 0;
        foreach (var l in labelled.Where(l => l.Decision.Shadow))
        {
            total++;
            var wakes = l.Decision.Decision == GateDecisions.Wake;
            if (l.ShouldWake)
            {
                positives++;
                if (!wakes)
                {
                    miss++;
                }
            }

            if (wakes && l.Decision.Path == GatePaths.Scored)
            {
                promote++;
                if (l.ShouldWake)
                {
                    truePromote++;
                }
            }
        }

        int? upper = positives == 0 ? null : (int)Math.Ceiling(WilsonUpper(miss, positives) * 10000);
        return new ShadowMetrics(
            total,
            promote,
            truePromote,
            promote - truePromote,
            miss,
            promote == 0 ? null : (int)Math.Round(truePromote * 10000.0 / promote),
            positives == 0 ? null : (int)Math.Round(miss * 10000.0 / positives),
            upper,
            total >= minLabels && upper is { } u && u <= maxMissBoundBasisPoints);
    }

    private static double WilsonUpper(int successes, int n)
    {
        const double z = 1.96;
        var p = (double)successes / n;
        var denom = 1 + (z * z / n);
        var centre = p + (z * z / (2 * n));
        var margin = z * Math.Sqrt((p * (1 - p) / n) + (z * z / (4.0 * n * n)));
        return Math.Min(1.0, (centre + margin) / denom);
    }
}
