using Bazaar.Kodo.Probes;
using Bazaar.Kodo.Probes.Host;
using Bazaar.Kodo.Probes.Self;
using Bazaar.Kodo.Tests.Probes.Host;
using Bazaar.Kodo.Tests.Probes.Self;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Kodo.Tests.Probes;

/// <summary>
/// Built-in probes run through the probe runtime (doc 46 section 7). A hung read is cancelled by its timeout and reported
/// as unknown, and nothing that fails to read reaches green.
/// </summary>
public sealed class BuiltInProbeRunnerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TimeSpan _probeTimeout = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task AHungDiskReadTimesOutAsUnknownNotGreen()
    {
        var time = new FakeTimeProvider();
        var readings = new FakeHostReadings
        {
            Disk = async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return new DiskReading(90, 100);
            },
        };
        var runner = NewRunner(time);
        var probe = new DiskFreeProbe(readings, "data");

        Task<ProbeResult> running = runner.RunAsync(probe.RunAsync, _probeTimeout, Ct);
        time.Advance(_probeTimeout);
        ProbeResult result = await running;

        Assert.Equal(ProbeStatus.Unknown, result.Status);
        Assert.Equal(ProbeCauses.Timeout, result.Cause);
    }

    [Fact]
    public async Task AHungJournalWriteTimesOutAsUnknownNotGreen()
    {
        var time = new FakeTimeProvider();
        var readings = new FakeKodoSelfReadings
        {
            JournalWritable = async ct =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return true;
            },
        };
        var runner = NewRunner(time);
        var probe = new JournalWritableProbe(readings);

        Task<ProbeResult> running = runner.RunAsync(probe.RunAsync, _probeTimeout, Ct);
        time.Advance(_probeTimeout);
        ProbeResult result = await running;

        Assert.Equal(ProbeStatus.Unknown, result.Status);
        Assert.Equal(ProbeCauses.Timeout, result.Cause);
    }

    [Fact]
    public async Task AFailedMemoryReadThroughTheRunnerIsUnknown()
    {
        var readings = new FakeHostReadings
        {
            Memory = _ => throw new IOException("counter unavailable"),
        };
        var runner = NewRunner(new FakeTimeProvider());
        var probe = new MemoryPressureProbe(readings);

        ProbeResult result = await runner.RunAsync(probe.RunAsync, _probeTimeout, Ct);

        Assert.Equal(ProbeStatus.Unknown, result.Status);
        Assert.Equal(ProbeCauses.Unreadable, result.Cause);
    }

    [Fact]
    public async Task AHealthyClockThroughTheRunnerIsGreen()
    {
        var runner = NewRunner(new FakeTimeProvider());
        var probe = new ClockDriftProbe(new FakeHostReadings());

        ProbeResult result = await runner.RunAsync(probe.RunAsync, _probeTimeout, Ct);

        Assert.Equal(ProbeStatus.Green, result.Status);
    }

    private static ProbeRunner NewRunner(TimeProvider time)
    {
        return new ProbeRunner(
            new Bulkhead("local", maxConcurrency: 8, timeProvider: time),
            new CircuitBreaker(CircuitBreaker.DefaultFailureThreshold, timeProvider: time));
    }
}
