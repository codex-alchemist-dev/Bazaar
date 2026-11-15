using Bazaar.Kodo.Probes;
using Bazaar.Kodo.Probes.Self;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Kodo.Tests.Probes.Self;

public sealed class SpillAgeProbeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset _now = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan _warnAge = TimeSpan.FromHours(1);

    [Fact]
    public void NoSpillFileIsGreen()
    {
        ProbeResult result = SpillAgeProbe.Evaluate(null, _now, _warnAge);

        Assert.Equal(ProbeStatus.Green, result.Status);
    }

    [Fact]
    public void ASealedSpillIsGreenHoweverOld()
    {
        var sealedSpill = new SpillFileReading(_now.AddDays(-30), Sealed: true);

        ProbeResult result = SpillAgeProbe.Evaluate(sealedSpill, _now, _warnAge);

        Assert.Equal(ProbeStatus.Green, result.Status);
    }

    [Theory]
    [InlineData(59, ProbeStatus.Green, null)]
    [InlineData(60, ProbeStatus.Amber, ProbeCauses.SpillAge)]
    [InlineData(600, ProbeStatus.Amber, ProbeCauses.SpillAge)]
    public void UnsealedAgeMapsToStatusAtTheWarningEdge(int ageMinutes, ProbeStatus status, string? cause)
    {
        var spill = new SpillFileReading(_now.AddMinutes(-ageMinutes), Sealed: false);

        ProbeResult result = SpillAgeProbe.Evaluate(spill, _now, _warnAge);

        Assert.Equal(status, result.Status);
        Assert.Equal(cause, result.Cause);
    }

    [Fact]
    public void FutureTimestampIsUnknownNeverGreen()
    {
        var spill = new SpillFileReading(_now.AddMinutes(5), Sealed: false);

        ProbeResult result = SpillAgeProbe.Evaluate(spill, _now, _warnAge);

        Assert.Equal(ProbeStatus.Unknown, result.Status);
        Assert.Equal(ProbeCauses.SpillTimestampFuture, result.Cause);
    }

    [Fact]
    public async Task RunAsyncAgesTheSpillWithTheInjectedClock()
    {
        var time = new FakeTimeProvider(_now);
        var readings = new FakeKodoSelfReadings
        {
            Spill = _ => Task.FromResult<SpillFileReading?>(new SpillFileReading(_now, Sealed: false)),
        };
        var probe = new SpillAgeProbe(readings, timeProvider: time);

        Assert.Equal(ProbeStatus.Green, (await probe.RunAsync(Ct)).Status);

        time.Advance(TimeSpan.FromHours(1));
        ProbeResult aged = await probe.RunAsync(Ct);

        Assert.Equal(ProbeStatus.Amber, aged.Status);
        Assert.Equal(ProbeCauses.SpillAge, aged.Cause);
    }

    [Fact]
    public async Task ReadFailureIsUnknownNeverGreen()
    {
        var readings = new FakeKodoSelfReadings
        {
            Spill = _ => throw new IOException("spill locked"),
        };

        ProbeResult result = await new SpillAgeProbe(readings, timeProvider: new FakeTimeProvider(_now)).RunAsync(Ct);

        Assert.Equal(ProbeStatus.Unknown, result.Status);
        Assert.Equal(ProbeCauses.Unreadable, result.Cause);
    }

    [Fact]
    public void DefaultWarnAgeIsTheProposedOneHour()
    {
        var probe = new SpillAgeProbe(new FakeKodoSelfReadings());

        Assert.Equal("kodo.self.spill", SpillAgeProbe.Id);
        Assert.Equal(TimeSpan.FromHours(1), probe.WarnAge);
    }

    [Fact]
    public void WarnAgeMustBePositive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpillAgeProbe(new FakeKodoSelfReadings(), warnAge: TimeSpan.Zero));
    }
}
