using Bazaar.Kodo.Probes;
using Bazaar.Kodo.Probes.Self;

namespace Bazaar.Kodo.Tests.Probes.Self;

public sealed class JournalWritableProbeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task WritableJournalIsGreen()
    {
        var probe = new JournalWritableProbe(new FakeKodoSelfReadings());

        ProbeResult result = await probe.RunAsync(Ct);

        Assert.Equal(ProbeStatus.Green, result.Status);
        Assert.Equal("kodo.self.journal_writable", JournalWritableProbe.Id);
    }

    [Fact]
    public async Task RefusedWriteIsRedWithTheJournalCause()
    {
        var readings = new FakeKodoSelfReadings { JournalWritable = _ => Task.FromResult(false) };

        ProbeResult result = await new JournalWritableProbe(readings).RunAsync(Ct);

        Assert.Equal(ProbeStatus.Red, result.Status);
        Assert.Equal(ProbeCauses.JournalNotWritable, result.Cause);
    }

    [Fact]
    public async Task AWriteAttemptThatCannotRunIsUnknownNeverGreen()
    {
        var readings = new FakeKodoSelfReadings
        {
            JournalWritable = _ => throw new UnauthorizedAccessException("denied to the probe itself"),
        };

        ProbeResult result = await new JournalWritableProbe(readings).RunAsync(Ct);

        Assert.Equal(ProbeStatus.Unknown, result.Status);
        Assert.Equal(ProbeCauses.Unreadable, result.Cause);
    }
}
