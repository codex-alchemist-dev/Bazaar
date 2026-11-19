using Bazaar.Kodo.Probes.Host;

namespace Bazaar.Kodo.Tests.Probes.Host;

/// <summary>A scriptable <see cref="IHostReadings"/>. Each read can be replaced by a test with a value or a throw.</summary>
internal sealed class FakeHostReadings : IHostReadings
{
    public Func<string, CancellationToken, Task<DiskReading>> Disk { get; set; } =
        (_, _) => Task.FromResult(new DiskReading(FreeBytes: 50, TotalBytes: 100));

    public Func<CancellationToken, Task<MemoryReading>> Memory { get; set; } =
        _ => Task.FromResult(new MemoryReading(AvailableBytes: 50, TotalBytes: 100));

    public Func<CancellationToken, Task<TimeSpan>> ClockOffset { get; set; } =
        _ => Task.FromResult(TimeSpan.Zero);

    public Task<DiskReading> ReadDiskAsync(string volume, CancellationToken cancellationToken) =>
        Disk(volume, cancellationToken);

    public Task<MemoryReading> ReadMemoryAsync(CancellationToken cancellationToken) =>
        Memory(cancellationToken);

    public Task<TimeSpan> ReadClockOffsetAsync(CancellationToken cancellationToken) =>
        ClockOffset(cancellationToken);
}
