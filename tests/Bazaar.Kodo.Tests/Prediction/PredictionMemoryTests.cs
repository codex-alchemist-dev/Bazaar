using System.Text;
using Bazaar.Kodo.Prediction;
using Bazaar.Kodo.Probes;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Kodo.Tests.Prediction;

public sealed class PredictionMemoryTests
{
    private static readonly TimeSpan _hour = TimeSpan.FromHours(1);

    [Fact]
    public void TrendProbeSurvivesARestartThroughJson()
    {
        var clock = new FakeTimeProvider(PredictionTestData.Start);
        var probe = new TrendForecastProbe("disk.trend", clock, 200, ThresholdDirection.Rising);
        for (int i = 0; i < 100; i++)
        {
            probe.Record(10 + i);
            if (i < 99)
            {
                clock.Advance(_hour);
            }
        }

        ProbeResult before = probe.Evaluate();
        byte[] bytes = PredictionMemoryCodec.ToUtf8Bytes(probe.ToMemory());

        Assert.True(PredictionMemoryCodec.TryParse(bytes, out PredictionMemory? parsed));
        var fresh = new TrendForecastProbe("disk.trend", clock, 200, ThresholdDirection.Rising);
        Assert.Equal(ProbeStatus.Unknown, fresh.Evaluate().Status);
        Assert.True(fresh.TryRestore(parsed));

        Assert.Equal(before, fresh.Evaluate());
        Assert.Equal(probe.LastForecast, fresh.LastForecast);
        Assert.Equal(probe.ToMemory().Trend!.Samples, fresh.ToMemory().Trend!.Samples);
    }

    [Fact]
    public void DriftProbeSurvivesARestartThroughJson()
    {
        var clock = new FakeTimeProvider(PredictionTestData.Start);
        var probe = new BaselineDriftProbe("lat.drift", clock);
        for (int h = 0; h < (24 * 7 * 4) + 1; h++)
        {
            probe.Observe(100);
            clock.Advance(_hour);
        }

        byte[] bytes = PredictionMemoryCodec.ToUtf8Bytes(probe.ToMemory());
        Assert.True(PredictionMemoryCodec.TryParse(bytes, out PredictionMemory? parsed));
        var restored = new BaselineDriftProbe("lat.drift", clock);
        Assert.True(restored.TryRestore(parsed));

        // Both continue identically, including through a drift episode.
        for (int h = 0; h < 6; h++)
        {
            Assert.Equal(probe.Observe(150), restored.Observe(150));
            clock.Advance(_hour);
        }

        Assert.Equal(DriftState.DriftUp, restored.LastAssessment!.State);
        Assert.Equal(probe.LastAssessment, restored.LastAssessment);
    }

    [Fact]
    public void ActiveDriftIsKeptAcrossARestart()
    {
        var clock = new FakeTimeProvider(PredictionTestData.Start);
        var probe = new BaselineDriftProbe("lat.drift", clock);
        for (int h = 0; h < 24 * 7 * 4; h++)
        {
            probe.Observe(100);
            clock.Advance(_hour);
        }

        probe.Observe(150);
        clock.Advance(_hour);
        PredictionMemory memory = probe.ToMemory();
        Assert.True(memory.Drift!.Positive > 0);
        Assert.NotNull(memory.Drift.PositiveStart);

        var restored = new BaselineDriftProbe("lat.drift", clock);
        Assert.True(PredictionMemoryCodec.TryParse(PredictionMemoryCodec.ToUtf8Bytes(memory), out PredictionMemory? parsed));
        Assert.True(restored.TryRestore(parsed));

        Assert.Equal(ProbeStatus.Amber, restored.Observe(150).Status);
    }

    [Fact]
    public void JsonIsCamelCaseAndCompact()
    {
        var probe = new TrendForecastProbe("x", new FakeTimeProvider(PredictionTestData.Start), 1, ThresholdDirection.Rising);
        probe.Record(PredictionTestData.Start, 2);

        string json = Encoding.UTF8.GetString(PredictionMemoryCodec.ToUtf8Bytes(probe.ToMemory()));

        Assert.Contains("\"version\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"samples\":[{\"at\":", json, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', json);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"version\":2,\"trend\":null,\"baseline\":null,\"drift\":null}")]
    [InlineData("{\"version\":1,\"trend\":null,\"baseline\":null,\"drift\":null,\"extra\":1}")]
    [InlineData("{\"version\":1,\"trend\":{\"samples\":[{\"at\":1,\"value\":NaN}]},\"baseline\":null,\"drift\":null}")]
    [InlineData("{\"version\":1,\"trend\":{\"samples\":[{\"at\":\"x\",\"value\":1}]},\"baseline\":null,\"drift\":null}")]
    public void CodecRejectsBadInputWithoutThrowing(string json)
    {
        Assert.False(PredictionMemoryCodec.TryParse(Encoding.UTF8.GetBytes(json), out PredictionMemory? memory));
        Assert.Null(memory);
    }

    [Fact]
    public void CodecRejectsOversizedInput()
    {
        byte[] big = new byte[PredictionMemoryCodec.MaxBytes + 1];
        Array.Fill(big, (byte)' ');

        Assert.False(PredictionMemoryCodec.TryParse(big, out _));
    }

    [Fact]
    public void DamagedMemoryIsRefusedAndKeepsCurrentState()
    {
        var clock = new FakeTimeProvider(PredictionTestData.Start);
        var probe = new BaselineDriftProbe("x", clock);
        probe.Observe(5);
        string before = Convert.ToHexString(PredictionMemoryCodec.ToUtf8Bytes(probe.ToMemory()));
        var v = PredictionMemory.CurrentVersion;

        Assert.False(probe.TryRestore(null));
        Assert.False(probe.TryRestore(new PredictionMemory(99, null, new BaselineMemory(null, []), new DriftMemory(0, 0, null, null))));
        Assert.False(probe.TryRestore(new PredictionMemory(v, null, null, null)));
        Assert.False(probe.TryRestore(new PredictionMemory(v, null, new BaselineMemory(null, [new BucketRecord(500, [])]), new DriftMemory(0, 0, null, null))));
        Assert.False(probe.TryRestore(new PredictionMemory(v, null, new BaselineMemory(null, [new BucketRecord(1, [new BucketEntryRecord(3, 1), new BucketEntryRecord(3, 2)])]), new DriftMemory(0, 0, null, null))));
        Assert.False(probe.TryRestore(new PredictionMemory(v, null, new BaselineMemory(null, []), new DriftMemory(-1, 0, null, null))));
        Assert.False(probe.TryRestore(new PredictionMemory(v, null, new BaselineMemory(null, []), new DriftMemory(double.NaN, 0, null, null))));
        Assert.False(probe.TryRestore(new PredictionMemory(v, null, new BaselineMemory(null, []), new DriftMemory(1e9, 0, 5, null))));
        Assert.False(probe.TryRestore(new PredictionMemory(v, null, new BaselineMemory(null, []), new DriftMemory(0, 0, 5, null))));

        Assert.Equal(before, Convert.ToHexString(PredictionMemoryCodec.ToUtf8Bytes(probe.ToMemory())));
    }

    [Fact]
    public void ResetForgetsEverythingLearned()
    {
        var clock = new FakeTimeProvider(PredictionTestData.Start);
        var probe = new BaselineDriftProbe("x", clock);
        for (int h = 0; h < 24 * 7 * 4; h++)
        {
            probe.Observe(100);
            clock.Advance(_hour);
        }

        probe.Reset();

        Assert.Empty(probe.ToMemory().Baseline!.Buckets);
        Assert.Equal(ProbeStatus.Unknown, probe.Observe(100).Status);
    }

    [Fact]
    public void TrendProbeRejectionStaysUnknownUntilTheNextGoodSample()
    {
        var clock = new FakeTimeProvider(PredictionTestData.Start);
        var probe = new TrendForecastProbe("x", clock, 1000, ThresholdDirection.Rising);
        for (int i = 0; i < 100; i++)
        {
            probe.Record(i);
            if (i < 99)
            {
                clock.Advance(_hour);
            }
        }

        Assert.Equal(ProbeStatus.Green, probe.Evaluate().Status);

        Assert.Equal(SampleAcceptance.Invalid, probe.Record(double.NaN));
        ProbeResult bad = probe.Evaluate();
        Assert.Equal(ProbeStatus.Unknown, bad.Status);
        Assert.Equal(PredictionCauses.InvalidSample, bad.Cause);

        clock.Advance(_hour);
        Assert.Equal(SampleAcceptance.Accepted, probe.Record(100));
        Assert.NotEqual(ProbeStatus.Unknown, probe.Evaluate().Status);
    }

    [Fact]
    public void TrendProbeUsesTheTimeProviderForStaleness()
    {
        var clock = new FakeTimeProvider(PredictionTestData.Start);
        var probe = new TrendForecastProbe("x", clock, 1000, ThresholdDirection.Rising);
        for (int i = 0; i < 100; i++)
        {
            probe.Record(i);
            clock.Advance(_hour);
        }

        clock.Advance(TimeSpan.FromDays(2));

        ProbeResult result = probe.Evaluate();
        Assert.Equal(ProbeStatus.Unknown, result.Status);
        Assert.Equal(PredictionCauses.StaleData, result.Cause);
    }

    [Fact]
    public void ProbesReportRisingTrendAsAmberWithCause()
    {
        var clock = new FakeTimeProvider(PredictionTestData.Start);
        var probe = new TrendForecastProbe("disk", clock, 200, ThresholdDirection.Rising);
        for (int i = 0; i < 100; i++)
        {
            probe.Record(10 + i);
            if (i < 99)
            {
                clock.Advance(_hour);
            }
        }

        ProbeResult result = probe.Evaluate();

        Assert.Equal(ProbeStatus.Amber, result.Status);
        Assert.Equal(PredictionCauses.ForecastThresholdSoon, result.Cause);
        Assert.NotEqual(ProbeStatus.Red, result.Status);
    }
}
