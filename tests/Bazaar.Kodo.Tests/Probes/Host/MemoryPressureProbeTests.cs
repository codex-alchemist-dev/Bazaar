using Bazaar.Kodo.Probes;
using Bazaar.Kodo.Probes.Host;

namespace Bazaar.Kodo.Tests.Probes.Host;

public sealed class MemoryPressureProbeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly UsedPercentThresholds _thresholds = new(warn: 90, high: 97);

    [Theory]
    [InlineData(11L, 100L, ProbeStatus.Green, null)]
    [InlineData(10L, 100L, ProbeStatus.Amber, ProbeCauses.MemoryPressureWarn)]
    [InlineData(4L, 100L, ProbeStatus.Amber, ProbeCauses.MemoryPressureWarn)]
    [InlineData(3L, 100L, ProbeStatus.Red, ProbeCauses.MemoryPressureHigh)]
    [InlineData(0L, 100L, ProbeStatus.Red, ProbeCauses.MemoryPressureHigh)]
    public void UsedPercentMapsToStatusAtTheThresholdEdges(long available, long total, ProbeStatus status, string? cause)
    {
        ProbeResult result = MemoryPressureProbe.Evaluate(new MemoryReading(available, total), _thresholds);

        Assert.Equal(status, result.Status);
        Assert.Equal(cause, result.Cause);
    }

    [Theory]
    [InlineData(0L, 0L)]
    [InlineData(-1L, 100L)]
    [InlineData(101L, 100L)]
    public void ImpossibleReadingIsUnknownNeverGreen(long available, long total)
    {
        ProbeResult result = MemoryPressureProbe.Evaluate(new MemoryReading(available, total), _thresholds);

        Assert.Equal(ProbeStatus.Unknown, result.Status);
        Assert.Equal(ProbeCauses.Unreadable, result.Cause);
    }

    [Fact]
    public async Task RunAsyncMapsTheReadingAndTurnsAReadFailureIntoUnknown()
    {
        var pressured = new FakeHostReadings
        {
            Memory = _ => Task.FromResult(new MemoryReading(AvailableBytes: 2, TotalBytes: 100)),
        };
        var failing = new FakeHostReadings
        {
            Memory = _ => throw new InvalidOperationException("counter missing"),
        };

        ProbeResult red = await new MemoryPressureProbe(pressured).RunAsync(Ct);
        ProbeResult unknown = await new MemoryPressureProbe(failing).RunAsync(Ct);

        Assert.Equal(ProbeStatus.Red, red.Status);
        Assert.Equal(ProbeStatus.Unknown, unknown.Status);
        Assert.Equal(ProbeCauses.Unreadable, unknown.Cause);
    }

    [Fact]
    public void DefaultThresholdsAreTheProposedValues()
    {
        var probe = new MemoryPressureProbe(new FakeHostReadings());

        Assert.Equal("host.memory.pressure", MemoryPressureProbe.Id);
        Assert.Equal(90, probe.Thresholds.Warn);
        Assert.Equal(97, probe.Thresholds.High);
    }
}
