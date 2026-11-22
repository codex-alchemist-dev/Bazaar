using Bazaar.Kodo.Prediction;
using Bazaar.Kodo.Probes;

namespace Bazaar.Kodo.Tests.Prediction;

public sealed class TrendForecasterTests
{
    private static readonly TimeSpan _hour = TimeSpan.FromHours(1);
    private static readonly TrendOptions _options = new();

    private static TrendForecast Run(
        List<TrendSample> samples,
        double threshold,
        ThresholdDirection direction = ThresholdDirection.Rising,
        TrendOptions? options = null,
        TimeSpan? after = null)
    {
        DateTimeOffset now = samples[^1].At + (after ?? TimeSpan.Zero);
        return TrendForecaster.Forecast(samples, threshold, direction, options ?? _options, now);
    }

    [Fact]
    public void LinearRiseGivesTheExactTimeToThresholdWithATightBand()
    {
        List<TrendSample> samples = PredictionTestData.Series(100, _hour, i => 10 + i);

        TrendForecast forecast = Run(samples, 200);

        Assert.Equal(ForecastOutcome.Projected, forecast.Outcome);
        Assert.Equal(ForecastConfidence.High, forecast.Confidence);
        Assert.Equal(91 * 3600, forecast.Estimate!.Value.TotalSeconds, 1);
        Assert.Equal(forecast.Estimate, forecast.Earliest);
        Assert.Equal(forecast.Estimate, forecast.Latest);
        Assert.Equal(ProbeStatus.Amber, PredictionProbeMapper.ToProbeResult(forecast, _options).Status);
    }

    [Fact]
    public void FallingSeriesForecastsTowardsALowerThreshold()
    {
        List<TrendSample> samples = PredictionTestData.Series(100, _hour, i => 500 - (2 * i));

        TrendForecast forecast = Run(samples, 100, ThresholdDirection.Falling);

        Assert.Equal(ForecastOutcome.Projected, forecast.Outcome);
        Assert.Equal(101 * 3600, forecast.Estimate!.Value.TotalSeconds, 1);
    }

    [Fact]
    public void ExactlyAtTheThresholdIsBreachedAndOneStepBelowIsProjected()
    {
        List<TrendSample> atThreshold = PredictionTestData.Series(100, _hour, i => i);
        List<TrendSample> below = PredictionTestData.Series(100, _hour, i => i);

        Assert.Equal(ForecastOutcome.Breached, Run(atThreshold, 99).Outcome);
        Assert.Equal(TimeSpan.Zero, Run(atThreshold, 99).Estimate);
        Assert.Equal(ForecastOutcome.Projected, Run(below, 99.5).Outcome);
        Assert.Equal(ForecastOutcome.Breached, Run(below, 50).Outcome);
        Assert.Equal(ProbeStatus.Amber, PredictionProbeMapper.ToProbeResult(Run(atThreshold, 99), _options).Status);
    }

    [Fact]
    public void FallingSeriesAtOrBelowTheThresholdIsBreached()
    {
        List<TrendSample> samples = PredictionTestData.Series(100, _hour, i => 200 - i);

        Assert.Equal(ForecastOutcome.Breached, Run(samples, 101, ThresholdDirection.Falling).Outcome);
        Assert.Equal(ForecastOutcome.Projected, Run(samples, 100, ThresholdDirection.Falling).Outcome);
    }

    [Theory]
    [InlineData(24, 3.0, false)] // 24 samples over 69 hours: enough samples, span short of 72 hours
    [InlineData(25, 3.0, true)] // 25 samples over exactly 72 hours
    [InlineData(23, 4.0, false)] // 23 samples over 88 hours: span fine, one sample short of the minimum
    public void MinimumSamplesAndMinimumSpanAreBothRequired(int count, double stepHours, bool forecasts)
    {
        List<TrendSample> samples = PredictionTestData.Series(count, TimeSpan.FromHours(stepHours), i => i);

        TrendForecast forecast = Run(samples, 1000);

        Assert.Equal(forecasts, forecast.Outcome != ForecastOutcome.Unknown);
        if (!forecasts)
        {
            Assert.Equal(PredictionCauses.InsufficientData, forecast.Cause);
            Assert.Equal(ProbeStatus.Unknown, PredictionProbeMapper.ToProbeResult(forecast, _options).Status);
        }
    }

    [Fact]
    public void OneSampleShortOfTheMinimumIsUnknownAndTheMinimumItselfForecasts()
    {
        List<TrendSample> atMinimum = PredictionTestData.Series(_options.MinSamples, TimeSpan.FromHours(4), i => i);
        List<TrendSample> oneShort = PredictionTestData.Series(_options.MinSamples - 1, TimeSpan.FromHours(4), i => i);

        Assert.Equal(ForecastOutcome.Projected, Run(atMinimum, 100).Outcome);
        TrendForecast forecast = Run(oneShort, 100);
        Assert.Equal(ForecastOutcome.Unknown, forecast.Outcome);
        Assert.Equal(PredictionCauses.InsufficientData, forecast.Cause);
    }

    [Fact]
    public void EmptyInputIsUnknown()
    {
        TrendForecast forecast = TrendForecaster.Forecast([], 10, ThresholdDirection.Rising, _options, PredictionTestData.Start);

        Assert.Equal(ForecastOutcome.Unknown, forecast.Outcome);
        Assert.Equal(PredictionCauses.InsufficientData, forecast.Cause);
    }

    [Fact]
    public void FlatSeriesIsNotApproachingAndGreen()
    {
        List<TrendSample> samples = PredictionTestData.Series(100, _hour, _ => 42);

        TrendForecast forecast = Run(samples, 100);

        Assert.Equal(ForecastOutcome.NotApproaching, forecast.Outcome);
        Assert.Equal(ForecastConfidence.High, forecast.Confidence);
        Assert.Null(forecast.Estimate);
        Assert.Equal(ProbeStatus.Green, PredictionProbeMapper.ToProbeResult(forecast, _options).Status);
    }

    [Fact]
    public void SeriesMovingAwayFromTheThresholdIsNotApproaching()
    {
        List<TrendSample> decreasing = PredictionTestData.Series(100, _hour, i => 500 - i);

        Assert.Equal(ForecastOutcome.NotApproaching, Run(decreasing, 600).Outcome);
        Assert.Equal(
            ForecastOutcome.NotApproaching,
            Run(PredictionTestData.Series(100, _hour, i => 500 + i), 100, ThresholdDirection.Falling).Outcome);
    }

    [Fact]
    public void NoisySeriesStillForecastsNearTheTrueTime()
    {
        List<TrendSample> samples = PredictionTestData.Series(100, _hour, i => 10 + i + ((((i * 7919) % 11) - 5) * 0.3));

        TrendForecast forecast = Run(samples, 200);

        Assert.Equal(ForecastOutcome.Projected, forecast.Outcome);
        double hours = forecast.Estimate!.Value.TotalHours;
        Assert.InRange(hours, 80, 100);
        Assert.True(forecast.Earliest <= forecast.Estimate);
        Assert.True(forecast.Latest is null || forecast.Latest >= forecast.Estimate);
    }

    [Fact]
    public void FewOutliersDoNotMoveTheTrend()
    {
        List<TrendSample> samples = PredictionTestData.Series(100, _hour, i => 10 + i);
        samples[40] = samples[40] with { Value = 5000 };
        samples[41] = samples[41] with { Value = -5000 };

        TrendForecast forecast = Run(samples, 200);

        Assert.Equal(ForecastOutcome.Projected, forecast.Outcome);
        Assert.Equal(91, forecast.Estimate!.Value.TotalHours, 0.5);
    }

    [Fact]
    public void SlowTrendBeyondTheAmberHorizonIsGreen()
    {
        List<TrendSample> samples = PredictionTestData.Series(100, _hour, i => i * 0.02);

        TrendForecast forecast = Run(samples, 100);

        Assert.Equal(ForecastOutcome.Projected, forecast.Outcome);
        Assert.True(forecast.Estimate > _options.AmberHorizon);
        Assert.Equal(ProbeStatus.Green, PredictionProbeMapper.ToProbeResult(forecast, _options).Status);
    }

    [Fact]
    public void EstimateBeyondTheForecastCapIsNotApproaching()
    {
        List<TrendSample> samples = PredictionTestData.Series(100, _hour, i => i * 1e-9);

        Assert.Equal(ForecastOutcome.NotApproaching, Run(samples, 100).Outcome);
    }

    [Fact]
    public void LowConfidenceDoesNotRaiseAmber()
    {
        // Alternating slope: a wide band whose shallow end never reaches the threshold.
        List<TrendSample> samples = PredictionTestData.Series(100, _hour, i => (i * 0.5) + ((i % 2) * 40));

        TrendForecast forecast = Run(samples, 200);

        if (forecast.Outcome == ForecastOutcome.Projected && forecast.Confidence == ForecastConfidence.Low)
        {
            Assert.Equal(ProbeStatus.Green, PredictionProbeMapper.ToProbeResult(forecast, _options).Status);
        }

        Assert.NotEqual(ForecastOutcome.Unknown, forecast.Outcome);
    }

    [Fact]
    public void NaNSampleFailsToUnknown()
    {
        List<TrendSample> samples = PredictionTestData.Series(100, _hour, i => i);
        samples[10] = samples[10] with { Value = double.NaN };

        TrendForecast forecast = Run(samples, 500);

        Assert.Equal(ForecastOutcome.Unknown, forecast.Outcome);
        Assert.Equal(PredictionCauses.InvalidSample, forecast.Cause);
    }

    [Theory]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(1e300)]
    [InlineData(double.MaxValue)]
    [InlineData(-1e16)]
    public void InfiniteAndHugeValuesAreRejected(double bad)
    {
        List<TrendSample> samples = PredictionTestData.Series(100, _hour, i => i);
        samples[99] = samples[99] with { Value = bad };

        TrendForecast forecast = Run(samples, 500);

        Assert.Equal(ForecastOutcome.Unknown, forecast.Outcome);
        Assert.Equal(PredictionCauses.InvalidSample, forecast.Cause);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(1e300)]
    public void BadThresholdIsUnknown(double threshold)
    {
        List<TrendSample> samples = PredictionTestData.Series(100, _hour, i => i);

        TrendForecast forecast = Run(samples, threshold);

        Assert.Equal(ForecastOutcome.Unknown, forecast.Outcome);
        Assert.Equal(PredictionCauses.InvalidSample, forecast.Cause);
    }

    [Fact]
    public void LargeButAcceptedValuesStillForecast()
    {
        List<TrendSample> samples = PredictionTestData.Series(100, _hour, i => 1e14 + (i * 1e9));

        TrendForecast forecast = Run(samples, 1e14 + 2e11);

        Assert.Equal(ForecastOutcome.Projected, forecast.Outcome);
        Assert.Equal(101 * 3600, forecast.Estimate!.Value.TotalSeconds, 0);
    }

    [Fact]
    public void OutOfOrderAndDuplicateTimestampsAreUnknown()
    {
        List<TrendSample> swapped = PredictionTestData.Series(100, _hour, i => i);
        (swapped[20], swapped[21]) = (swapped[21], swapped[20]);
        List<TrendSample> duplicate = PredictionTestData.Series(100, _hour, i => i);
        duplicate[30] = duplicate[30] with { At = duplicate[29].At };

        Assert.Equal(PredictionCauses.OutOfOrderSample, Run(swapped, 500).Cause);
        Assert.Equal(PredictionCauses.OutOfOrderSample, Run(duplicate, 500).Cause);
    }

    [Fact]
    public void SampleFromTheFutureIsUnknownButSmallSkewIsTolerated()
    {
        List<TrendSample> samples = PredictionTestData.Series(100, _hour, i => i);
        DateTimeOffset end = samples[^1].At;

        TrendForecast future = TrendForecaster.Forecast(samples, 500, ThresholdDirection.Rising, _options, end - TimeSpan.FromMinutes(6));
        TrendForecast skew = TrendForecaster.Forecast(samples, 500, ThresholdDirection.Rising, _options, end - TimeSpan.FromMinutes(5));

        Assert.Equal(PredictionCauses.FutureSample, future.Cause);
        Assert.Equal(ForecastOutcome.Projected, skew.Outcome);
    }

    [Fact]
    public void StaleSeriesIsUnknownAtExactlyTheLimitPlusOneTick()
    {
        List<TrendSample> samples = PredictionTestData.Series(100, _hour, i => i);

        TrendForecast fresh = Run(samples, 500, after: _options.StaleAfter);
        TrendForecast stale = Run(samples, 500, after: _options.StaleAfter + TimeSpan.FromTicks(1));

        Assert.Equal(ForecastOutcome.Projected, fresh.Outcome);
        Assert.Equal(PredictionCauses.StaleData, stale.Cause);
    }

    [Fact]
    public void SamplesOlderThanTheWindowAreIgnored()
    {
        TrendOptions options = new() { Window = TimeSpan.FromDays(5), MinSpan = TimeSpan.FromHours(72) };
        // First 100 hours fall (steeply) outside the 5 day window, then 120 hours of a gentle trend.
        List<TrendSample> samples = PredictionTestData.Series(220, _hour, i => i < 100 ? i * 100 : 10 + (i - 100));

        TrendForecast forecast = Run(samples, 400, options: options);

        Assert.Equal(ForecastOutcome.Projected, forecast.Outcome);
        Assert.Equal(121, forecast.SampleCount);
    }

    [Fact]
    public void ResultsAreDeterministic()
    {
        List<TrendSample> samples = PredictionTestData.Series(100, _hour, i => 10 + i + ((((i * 7919) % 11) - 5) * 0.3));

        Assert.Equal(Run(samples, 200), Run(samples, 200));
    }

    [Fact]
    public void InvalidOptionsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TrendOptions { MinSamples = 2 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new TrendOptions { MaxSamples = 5 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new TrendOptions { MinSpan = TimeSpan.FromDays(30) }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new TrendOptions { MaxAbsValue = double.NaN }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new TrendOptions { AmberHorizon = TimeSpan.Zero }.Validate());
    }
}
