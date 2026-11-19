using Bazaar.Kodo.Probes;
using Bazaar.Kodo.Probes.Host;

namespace Bazaar.Kodo.Tests.Probes.Host;

public sealed class DiskFreeProbeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly UsedPercentThresholds _thresholds = new(warn: 80, high: 95);

    [Theory]
    [InlineData(21L, 100L, ProbeStatus.Green, null)]
    [InlineData(20L, 100L, ProbeStatus.Amber, ProbeCauses.DiskUsageWarn)]
    [InlineData(6L, 100L, ProbeStatus.Amber, ProbeCauses.DiskUsageWarn)]
    [InlineData(5L, 100L, ProbeStatus.Red, ProbeCauses.DiskUsageHigh)]
    [InlineData(0L, 100L, ProbeStatus.Red, ProbeCauses.DiskUsageHigh)]
    [InlineData(100L, 100L, ProbeStatus.Green, null)]
    public void UsedPercentMapsToStatusAtTheThresholdEdges(long free, long total, ProbeStatus status, string? cause)
    {
        ProbeResult result = DiskFreeProbe.Evaluate(new DiskReading(free, total), _thresholds);

        Assert.Equal(status, result.Status);
        Assert.Equal(cause, result.Cause);
    }

    [Theory]
    [InlineData(0L, 0L)]
    [InlineData(-1L, 100L)]
    [InlineData(101L, 100L)]
    public void ImpossibleReadingIsUnknownNeverGreen(long free, long total)
    {
        ProbeResult result = DiskFreeProbe.Evaluate(new DiskReading(free, total), _thresholds);

        Assert.Equal(ProbeStatus.Unknown, result.Status);
        Assert.Equal(ProbeCauses.Unreadable, result.Cause);
    }

    [Fact]
    public void IdCarriesTheVolumeNameAndDefaultsUseTheDocThreshold()
    {
        var probe = new DiskFreeProbe(new FakeHostReadings(), "data");

        Assert.Equal("host.disk.free.data", probe.Id);
        Assert.Equal(DiskFreeProbe.DefaultWarnUsedPercent, probe.Thresholds.Warn);
        Assert.Equal(80, probe.Thresholds.Warn);
    }

    [Fact]
    public async Task RunAsyncReadsTheRequestedVolume()
    {
        string? askedFor = null;
        var readings = new FakeHostReadings
        {
            Disk = (volume, _) =>
            {
                askedFor = volume;
                return Task.FromResult(new DiskReading(FreeBytes: 90, TotalBytes: 100));
            },
        };

        ProbeResult result = await new DiskFreeProbe(readings, "data").RunAsync(Ct);

        Assert.Equal("data", askedFor);
        Assert.Equal(ProbeStatus.Green, result.Status);
    }

    [Fact]
    public async Task ReadFailureIsUnknownAndNotRed()
    {
        var readings = new FakeHostReadings
        {
            Disk = (_, _) => throw new IOException("volume offline"),
        };

        ProbeResult result = await new DiskFreeProbe(readings, "data").RunAsync(Ct);

        Assert.Equal(ProbeStatus.Unknown, result.Status);
        Assert.Equal(ProbeCauses.Unreadable, result.Cause);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task CancellationIsRethrownNotReportedAsAResult()
    {
        var readings = new FakeHostReadings
        {
            Disk = async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return new DiskReading(90, 100);
            },
        };
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new DiskFreeProbe(readings, "data").RunAsync(cancelled.Token));
    }

    [Fact]
    public void ConstructorRejectsAnEmptyVolume()
    {
        Assert.Throws<ArgumentException>(() => new DiskFreeProbe(new FakeHostReadings(), " "));
    }

    [Fact]
    public void ThresholdsMustOrderWarnBeforeHighAndStayWithinPercent()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new UsedPercentThresholds(warn: 95, high: 95));
        Assert.Throws<ArgumentOutOfRangeException>(() => new UsedPercentThresholds(warn: 0, high: 95));
        Assert.Throws<ArgumentOutOfRangeException>(() => new UsedPercentThresholds(warn: 80, high: 101));
    }
}
