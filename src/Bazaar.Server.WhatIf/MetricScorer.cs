namespace Bazaar.Server.WhatIf;

/// <summary>Scores a branch measurement on the fixed metric spec. The result is 0 to 1; a heavier weight counts more.</summary>
public static class MetricScorer
{
    public static decimal Score(MetricSpec spec, BranchMeasurement m)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(m);
        spec.Validate();

        var parts = new Dictionary<string, decimal>
        {
            [MetricKeys.CiResult] = m.CiPassed ? 1m : 0m,
            [MetricKeys.DiffSize] = 1m - Fraction(m.DiffLines, spec.DiffSizeCapLines),
            [MetricKeys.ReviewBand] = (decimal)(int)m.Review / (int)ReviewBand.High,
            [MetricKeys.Tokens] = 1m - Fraction(m.Tokens, spec.TokenCap),
        };
        var total = spec.Weights.Values.Sum();
        var weighted = MetricKeys.All.Sum(k => spec.Weights[k] * parts[k]);
        return decimal.Round(weighted / total, 6, MidpointRounding.ToEven);
    }

    private static decimal Fraction(long value, long cap) => Math.Clamp((decimal)Math.Max(value, 0) / cap, 0m, 1m);
}
