using Bazaar.Contracts.Kodo;
using Bazaar.Kodo.Probes.Host;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Kodo.Tests;

internal sealed class FakeReadings : IHostReadings
{
    public int DiskUsedPercent { get; set; } = 40;

    public int MemoryUsedPercent { get; set; } = 40;

    public bool Throw { get; set; }

    public int DiskReads { get; private set; }

    public int MemoryReads { get; private set; }

    public Task<DiskReading> ReadDiskAsync(string volume, CancellationToken cancellationToken)
    {
        DiskReads++;
        if (Throw)
        {
            throw new IOException(@"C:\secret\path failed");
        }

        return Task.FromResult(new DiskReading(100 - DiskUsedPercent, 100));
    }

    public Task<MemoryReading> ReadMemoryAsync(CancellationToken cancellationToken)
    {
        MemoryReads++;
        return Task.FromResult(new MemoryReading(100 - MemoryUsedPercent, 100));
    }

    public Task<TimeSpan> ReadClockOffsetAsync(CancellationToken cancellationToken) => Task.FromResult(TimeSpan.Zero);
}

internal sealed class RecordingPublisher : IKodoStreamPublisher
{
    public List<KodoStreamEventDto> Events { get; } = [];

    public bool Throw { get; set; }

    public ValueTask PublishAsync(KodoStreamEventDto streamEvent, CancellationToken cancellationToken)
    {
        if (Throw)
        {
            throw new InvalidOperationException("transport down");
        }

        lock (Events)
        {
            Events.Add(streamEvent);
        }

        return ValueTask.CompletedTask;
    }
}

internal static class Make
{
    public static readonly DateTimeOffset Start = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    public static KodoMonitor Monitor(FakeReadings readings, FakeTimeProvider time, IKodoStreamPublisher? publisher = null, KodoServerOptions? options = null) =>
        new(options ?? new KodoServerOptions(), readings, publisher ?? new NullKodoStreamPublisher(), time, NullLogger<KodoMonitor>.Instance);

    public static FakeTimeProvider Clock() => new(Start);
}
