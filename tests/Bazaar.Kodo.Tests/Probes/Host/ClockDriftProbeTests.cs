using Bazaar.Kodo.Probes;
using Bazaar.Kodo.Probes.Host;

namespace Bazaar.Kodo.Tests.Probes.Host;

public sealed class ClockDriftProbeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly ClockDriftThresholds _thresholds =
        new(warn: TimeSpan.FromMilliseconds(500), hard: TimeSpan.FromSeconds(5));

    [Theory]
    [InlineData(0L, ProbeStatus.Green, null)]
    [InlineData(499L, ProbeStatus.Green, null)]
    [InlineData(-499L, ProbeStatus.Green, null)]
    [InlineData(500L, ProbeStatus.Amber, ProbeCauses.ClockDriftWarn)]
    [InlineData(-500L, ProbeStatus.Amber, ProbeCauses.ClockDriftWarn)]
    [InlineData(4999L, ProbeStatus.Amber, ProbeCauses.ClockDriftWarn)]
    [InlineData(5000L, ProbeStatus.Red, ProbeCauses.ClockDriftHard)]
    [InlineData(-5000L, ProbeStatus.Red, ProbeCauses.ClockDriftHard)]
    public void AbsoluteOffsetMapsToStatusInBothDirections(long offsetMilliseconds, ProbeStatus status, string? cause)
    {
        ProbeResult result = ClockDriftProbe.Evaluate(TimeSpan.FromMilliseconds(offsetMilliseconds), _thresholds);

        Assert.Equal(status, result.Status);
        Assert.Equal(cause, result.Cause);
    }

    [Fact]
    public void ExtremeOffsetsAreRedWithoutOverflow()
    {
        Assert.Equal(ProbeStatus.Red, ClockDriftProbe.Evaluate(TimeSpan.MinValue, _thresholds).Status);
        Assert.Equal(ProbeStatus.Red, ClockDriftProbe.Evaluate(TimeSpan.MaxValue, _thresholds).Status);
    }

    [Fact]
    public async Task RunAsyncReadsTheOffsetAndTurnsAReadFailureIntoUnknown()
    {
        var drifted = new FakeHostReadings
        {
            ClockOffset = _ => Task.FromResult(TimeSpan.FromSeconds(-9)),
        };
        var failing = new FakeHostReadings
        {
            ClockOffset = _ => throw new IOException("no time source answered"),
        };

        ProbeResult red = await new ClockDriftProbe(drifted, _thresholds).RunAsync(Ct);
        ProbeResult unknown = await new ClockDriftProbe(failing, _thresholds).RunAsync(Ct);

        Assert.Equal(ProbeStatus.Red, red.Status);
        Assert.Equal(ProbeStatus.Unknown, unknown.Status);
        Assert.Equal(ProbeCauses.Unreadable, unknown.Cause);
    }

    [Fact]
    public void WarnMustBePositiveAndBelowHard()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ClockDriftThresholds(TimeSpan.Zero, TimeSpan.FromSeconds(5)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ClockDriftThresholds(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)));
    }
}
