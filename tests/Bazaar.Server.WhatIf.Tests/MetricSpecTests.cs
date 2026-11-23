namespace Bazaar.Server.WhatIf.Tests;

public sealed class MetricSpecTests
{
    [Fact]
    public void The_hash_is_the_same_whatever_order_the_weights_are_given_in()
    {
        var a = new MetricSpec(new Dictionary<string, decimal>
        {
            [MetricKeys.CiResult] = 0.4m,
            [MetricKeys.DiffSize] = 0.2m,
            [MetricKeys.ReviewBand] = 0.3m,
            [MetricKeys.Tokens] = 0.1m,
        }, 1000, 1_000_000);
        var b = new MetricSpec(new Dictionary<string, decimal>
        {
            [MetricKeys.Tokens] = 0.10m,
            [MetricKeys.ReviewBand] = 0.30m,
            [MetricKeys.DiffSize] = 0.20m,
            [MetricKeys.CiResult] = 0.40m,
        }, 1000, 1_000_000);

        Assert.Equal(a.ToCanonicalJson(), b.ToCanonicalJson());
        Assert.Equal(a.Hash(), b.Hash());
        Assert.Matches("^[0-9a-f]{64}$", a.Hash());
    }

    [Fact]
    public void A_different_cap_or_weight_changes_the_hash()
    {
        var baseline = MetricSpec.Default.Hash();

        Assert.NotEqual(baseline, (MetricSpec.Default with { TokenCap = 5 }).Hash());
        var heavier = new Dictionary<string, decimal>(MetricSpec.Default.Weights) { [MetricKeys.CiResult] = 0.5m };
        Assert.NotEqual(baseline, (MetricSpec.Default with { Weights = heavier }).Hash());
    }

    [Fact]
    public void An_incomplete_or_negative_spec_is_invalid()
    {
        Assert.Throws<ArgumentException>(() => (MetricSpec.Default with { Weights = new Dictionary<string, decimal> { [MetricKeys.CiResult] = 1m } }).Validate());
        var negative = new Dictionary<string, decimal>(MetricSpec.Default.Weights) { [MetricKeys.Tokens] = -1m };
        Assert.Throws<ArgumentException>(() => (MetricSpec.Default with { Weights = negative }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (MetricSpec.Default with { DiffSizeCapLines = 0 }).Validate());
    }

    [Fact]
    public void A_perfect_branch_scores_one_and_a_worst_branch_scores_zero()
    {
        var best = MetricScorer.Score(MetricSpec.Default, new BranchMeasurement(true, 0, ReviewBand.High, 0));
        var worst = MetricScorer.Score(MetricSpec.Default, new BranchMeasurement(false, 5000, ReviewBand.Low, 9_000_000));

        Assert.Equal(1m, best);
        Assert.Equal(0m, worst);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(0.5, true)]
    [InlineData(1, true)]
    [InlineData(-0.01, false)]
    [InlineData(1.01, false)]
    public void Reputation_weight_must_be_between_zero_and_one(double value, bool valid)
    {
        var weight = (decimal)value;
        if (valid)
            Assert.Equal(weight, new WhatIfSettings(weight).ReputationWeight);
        else
            Assert.Throws<ArgumentOutOfRangeException>(() => new WhatIfSettings(weight));
    }

    [Fact]
    public void Reputation_weight_defaults_to_zero()
    {
        Assert.Equal(0m, new WhatIfSettings().ReputationWeight);
    }
}
