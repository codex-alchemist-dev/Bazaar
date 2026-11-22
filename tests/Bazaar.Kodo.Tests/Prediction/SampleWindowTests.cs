using Bazaar.Kodo.Prediction;

namespace Bazaar.Kodo.Tests.Prediction;

public sealed class SampleWindowTests
{
    [Fact]
    public void MemoryIsBoundedByMaxSamples()
    {
        TrendOptions options = new() { MaxSamples = 50 };
        var window = new SampleWindow(options);

        for (int i = 0; i < 5000; i++)
        {
            Assert.Equal(SampleAcceptance.Accepted, window.TryAdd(PredictionTestData.Start + TimeSpan.FromMinutes(i), i));
        }

        Assert.Equal(50, window.Count);
        Assert.Equal(4999, window.Samples[^1].Value);
        Assert.Equal(4950, window.Samples[0].Value);
    }

    [Fact]
    public void SamplesOlderThanTheWindowAreDropped()
    {
        var window = new SampleWindow(new TrendOptions { Window = TimeSpan.FromDays(3), MinSpan = TimeSpan.FromHours(72) });

        for (int i = 0; i < 24 * 10; i++)
        {
            window.TryAdd(PredictionTestData.Start + TimeSpan.FromHours(i), i);
        }

        Assert.True(window.Samples[^1].At - window.Samples[0].At <= TimeSpan.FromDays(3));
        Assert.Equal(24 * 3 + 1, window.Count);
    }

    [Fact]
    public void RejectedSamplesChangeNothing()
    {
        var window = new SampleWindow(new TrendOptions());
        window.TryAdd(PredictionTestData.Start, 1);

        Assert.Equal(SampleAcceptance.Invalid, window.TryAdd(PredictionTestData.Start.AddHours(1), double.NaN));
        Assert.Equal(SampleAcceptance.Invalid, window.TryAdd(PredictionTestData.Start.AddHours(1), double.PositiveInfinity));
        Assert.Equal(SampleAcceptance.Invalid, window.TryAdd(PredictionTestData.Start.AddHours(1), 1e30));
        Assert.Equal(SampleAcceptance.OutOfOrder, window.TryAdd(PredictionTestData.Start, 2));
        Assert.Equal(SampleAcceptance.OutOfOrder, window.TryAdd(PredictionTestData.Start.AddHours(-1), 2));

        Assert.Equal(1, window.Count);
    }

    [Fact]
    public void TimesAtTheEdgeOfTheRepresentableRangeDoNotThrow()
    {
        var window = new SampleWindow(new TrendOptions());

        Assert.Equal(SampleAcceptance.Accepted, window.TryAdd(DateTimeOffset.MinValue, 1));
        Assert.Equal(SampleAcceptance.Accepted, window.TryAdd(DateTimeOffset.MaxValue, 2));
        TrendForecast forecast = TrendForecaster.Forecast(
            window.Samples, 10, ThresholdDirection.Rising, new TrendOptions(), DateTimeOffset.MinValue);
        Assert.Equal(ForecastOutcome.Unknown, forecast.Outcome);
    }

    [Fact]
    public void RestoreRoundTripsAndRejectsDamagedMemory()
    {
        var options = new TrendOptions();
        var window = new SampleWindow(options);
        for (int i = 0; i < 40; i++)
        {
            window.TryAdd(PredictionTestData.Start + TimeSpan.FromHours(i), i * 1.5);
        }

        Assert.True(SampleWindow.TryRestore(window.ToMemory(), options, out SampleWindow? restored));
        Assert.Equal(window.Samples, restored!.Samples);

        Assert.False(SampleWindow.TryRestore(null, options, out _));
        Assert.False(SampleWindow.TryRestore(new TrendMemory([new SampleRecord(5, 1), new SampleRecord(5, 2)]), options, out _));
        Assert.False(SampleWindow.TryRestore(new TrendMemory([new SampleRecord(5, double.NaN)]), options, out _));
        Assert.False(SampleWindow.TryRestore(new TrendMemory([new SampleRecord(long.MaxValue, 1)]), options, out _));
        Assert.False(SampleWindow.TryRestore(
            new TrendMemory([.. Enumerable.Range(1, options.MaxSamples + 1).Select(i => new SampleRecord(i * 1000L, i))]),
            options,
            out _));
    }
}
