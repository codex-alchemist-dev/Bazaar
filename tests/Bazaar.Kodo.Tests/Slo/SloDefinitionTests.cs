using Bazaar.Kodo.Evaluator;
using Bazaar.Kodo.Slo;

namespace Bazaar.Kodo.Tests.Slo;

public sealed class SloDefinitionTests
{
    [Theory]
    [InlineData("")]
    [InlineData("Svc")]
    [InlineData("1svc")]
    [InlineData("svc id")]
    [InlineData("svc.id")]
    public void An_id_that_is_not_an_identifier_is_refused(string id)
    {
        Assert.Throws<ArgumentException>(() => new SloDefinition(id, 990_000, TimeSpan.FromDays(30), SloIndicatorKind.Availability));
    }

    [Fact]
    public void The_target_has_a_floor_and_a_ceiling()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SloDefinition("svc", 899_999, TimeSpan.FromDays(30), SloIndicatorKind.Availability));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SloDefinition("svc", 1_000_000, TimeSpan.FromDays(30), SloIndicatorKind.Availability));
        Assert.Equal(100_000, new SloDefinition("svc", 900_000, TimeSpan.FromDays(30), SloIndicatorKind.Availability).AllowedPpm);
    }

    [Fact]
    public void The_window_is_bounded()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SloDefinition("svc", 990_000, TimeSpan.FromMinutes(59), SloIndicatorKind.Availability));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SloDefinition("svc", 990_000, TimeSpan.FromDays(32), SloIndicatorKind.Availability));
    }

    [Fact]
    public void Only_a_latency_slo_has_a_threshold()
    {
        Assert.Throws<ArgumentException>(() => new SloDefinition("svc", 990_000, TimeSpan.FromDays(30), SloIndicatorKind.LatencyThreshold));
        Assert.Throws<ArgumentException>(() => new SloDefinition("svc", 990_000, TimeSpan.FromDays(30), SloIndicatorKind.LatencyThreshold, TimeSpan.Zero));
        Assert.Throws<ArgumentException>(() => new SloDefinition("svc", 990_000, TimeSpan.FromDays(30), SloIndicatorKind.LatencyThreshold, TimeSpan.FromMinutes(2)));
        Assert.Throws<ArgumentException>(() => new SloDefinition("svc", 990_000, TimeSpan.FromDays(30), SloIndicatorKind.Availability, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void The_defaults_match_the_design_doc()
    {
        Assert.Equal(995_000, SloDefaults.ServerAvailability().TargetPpm);
        Assert.Equal(TimeSpan.FromDays(30), SloDefaults.ServerAvailability().Window);
        Assert.Equal(TimeSpan.FromMilliseconds(250), SloDefaults.GroupChatAppendLatency().LatencyThreshold);
        Assert.Equal(990_000, SloDefaults.StreamFreshness().TargetPpm);
        Assert.Equal("model_lane_provider_a", SloDefaults.ModelLaneAvailability("provider_a").Id);
        Assert.Throws<ArgumentException>(() => SloDefaults.ModelLaneAvailability("Bad Provider"));
    }

    [Fact]
    public void Readings_become_outcomes_and_doubt_is_unobserved()
    {
        Assert.Equal(SloSampleOutcome.Good, SloSamples.FromReadiness(true));
        Assert.Equal(SloSampleOutcome.Bad, SloSamples.FromReadiness(false));
        Assert.Equal(SloSampleOutcome.Unobserved, SloSamples.FromReadiness(null));
        Assert.Equal(SloSampleOutcome.Good, SloSamples.FromState(KodoState.Degraded));
        Assert.Equal(SloSampleOutcome.Bad, SloSamples.FromState(KodoState.Failing));
        Assert.Equal(SloSampleOutcome.Unobserved, SloSamples.FromState(KodoState.Frozen));
        Assert.Equal(SloSampleOutcome.Unobserved, SloSamples.FromState(null));
        Assert.Equal(SloSampleOutcome.Unobserved, SloSamples.FromState((KodoState)99));
        TimeSpan limit = TimeSpan.FromSeconds(5);
        Assert.Equal(SloSampleOutcome.Good, SloSamples.FromDuration(limit, limit));
        Assert.Equal(SloSampleOutcome.Bad, SloSamples.FromDuration(limit + TimeSpan.FromTicks(1), limit));
        Assert.Equal(SloSampleOutcome.Unobserved, SloSamples.FromDuration(null, limit));
        Assert.Equal(SloSampleOutcome.Unobserved, SloSamples.FromDuration(TimeSpan.FromSeconds(-1), limit));
    }
}
