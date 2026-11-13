using Bazaar.Server.Modules.Releases;

namespace Bazaar.Server.Tests.Modules.Releases;

/// <summary>Ring health gate: Continue, Hold and Halt boundaries (doc 49 section 6). Pure.</summary>
public sealed class RingHealthGateTests
{
    // Thresholds use exact binary fractions so the mean error rate is exact and the boundary is tested as written.
    private static readonly GateThresholds _exact = new(MinimumSamples: 4, MaxErrorRate: 0.25);

    private static HealthSample Sample(KodoHealthStatus status, double errorRate = 0, ReleaseRing ring = ReleaseRing.Canary) =>
        new(ring, status, errorRate);

    private static List<HealthSample> Green(int count, double errorRate = 0) =>
        Enumerable.Range(0, count).Select(_ => Sample(KodoHealthStatus.Green, errorRate)).ToList();

    [Fact]
    public void Continues_when_enough_green_samples_and_error_rate_is_within_threshold()
    {
        Assert.Equal(GateDecision.Continue, RingHealthGate.Evaluate(ReleaseRing.Canary, Green(4), _exact));
    }

    [Fact]
    public void Holds_just_below_the_minimum_sample_count()
    {
        Assert.Equal(GateDecision.Hold, RingHealthGate.Evaluate(ReleaseRing.Canary, Green(3), _exact));
    }

    [Fact]
    public void Continues_exactly_at_the_minimum_sample_count()
    {
        Assert.Equal(GateDecision.Continue, RingHealthGate.Evaluate(ReleaseRing.Canary, Green(4), _exact));
    }

    [Fact]
    public void Holds_with_no_samples()
    {
        Assert.Equal(GateDecision.Hold, RingHealthGate.Evaluate(ReleaseRing.Canary, [], _exact));
    }

    [Fact]
    public void Error_rate_equal_to_threshold_does_not_halt()
    {
        Assert.Equal(GateDecision.Continue, RingHealthGate.Evaluate(ReleaseRing.Canary, Green(4, errorRate: 0.25), _exact));
    }

    [Fact]
    public void Error_rate_just_above_threshold_halts()
    {
        var samples = Green(4, errorRate: 0.25);
        samples[0] = Sample(KodoHealthStatus.Green, errorRate: 0.5);

        // Mean is (0.5 + 3 * 0.25) / 4 = 0.3125, above 0.25.
        Assert.Equal(GateDecision.Halt, RingHealthGate.Evaluate(ReleaseRing.Canary, samples, _exact));
    }

    [Fact]
    public void Error_rate_is_judged_by_the_mean_not_a_single_sample()
    {
        var samples = new List<HealthSample>
        {
            Sample(KodoHealthStatus.Green, errorRate: 0.5),
            Sample(KodoHealthStatus.Green, errorRate: 0),
            Sample(KodoHealthStatus.Green, errorRate: 0),
            Sample(KodoHealthStatus.Green, errorRate: 0),
        };

        // One bad sample averages to 0.125, which is under 0.25.
        Assert.Equal(GateDecision.Continue, RingHealthGate.Evaluate(ReleaseRing.Canary, samples, _exact));
    }

    [Fact]
    public void Red_status_halts_even_with_one_sample()
    {
        Assert.Equal(GateDecision.Halt, RingHealthGate.Evaluate(ReleaseRing.Canary, [Sample(KodoHealthStatus.Red)], _exact));
    }

    [Fact]
    public void Red_status_halts_even_when_the_rest_are_green()
    {
        var samples = Green(10);
        samples.Add(Sample(KodoHealthStatus.Red));

        Assert.Equal(GateDecision.Halt, RingHealthGate.Evaluate(ReleaseRing.Canary, samples, _exact));
    }

    [Fact]
    public void Amber_status_holds_and_does_not_halt()
    {
        var samples = Green(4);
        samples[2] = Sample(KodoHealthStatus.Amber);

        Assert.Equal(GateDecision.Hold, RingHealthGate.Evaluate(ReleaseRing.Canary, samples, _exact));
    }

    [Fact]
    public void Halt_takes_precedence_over_hold_for_a_short_sample_list()
    {
        // Fewer samples than the minimum, but a red sample must still halt.
        Assert.Equal(GateDecision.Halt, RingHealthGate.Evaluate(ReleaseRing.Canary, [Sample(KodoHealthStatus.Amber), Sample(KodoHealthStatus.Red)], _exact));
    }

    [Fact]
    public void Sample_from_another_ring_is_rejected()
    {
        var samples = new List<HealthSample> { Sample(KodoHealthStatus.Green, ring: ReleaseRing.Early) };

        Assert.Throws<ArgumentException>(() => RingHealthGate.Evaluate(ReleaseRing.Canary, samples, _exact));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void Error_rate_outside_zero_to_one_is_rejected(double errorRate)
    {
        var samples = new List<HealthSample> { Sample(KodoHealthStatus.Green, errorRate) };

        Assert.Throws<ArgumentException>(() => RingHealthGate.Evaluate(ReleaseRing.Canary, samples, _exact));
    }

    [Fact]
    public void Invalid_thresholds_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RingHealthGate.Evaluate(ReleaseRing.Canary, [], new GateThresholds(MinimumSamples: 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => RingHealthGate.Evaluate(ReleaseRing.Canary, [], new GateThresholds(MaxErrorRate: 1.5)));
    }

    [Fact]
    public void Default_thresholds_hold_below_thirty_samples_and_continue_at_thirty()
    {
        Assert.Equal(GateDecision.Hold, RingHealthGate.Evaluate(ReleaseRing.Canary, Green(29), new GateThresholds()));
        Assert.Equal(GateDecision.Continue, RingHealthGate.Evaluate(ReleaseRing.Canary, Green(30), new GateThresholds()));
    }
}
