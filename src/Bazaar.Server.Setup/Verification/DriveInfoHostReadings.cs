namespace Bazaar.Server.Setup;

/// <summary>
/// An OS-agnostic <see cref="Bazaar.Kodo.Probes.Host.IHostReadings"/> for setup verification. Disk and memory come from the runtime
/// (<see cref="DriveInfo"/> and the GC memory info). The clock offset needs a reference time source that setup does not have, so it
/// throws and Kodo reports unknown, which setup shows as not evaluated rather than green.
/// </summary>
public sealed class DriveInfoHostReadings : Bazaar.Kodo.Probes.Host.IHostReadings
{
    /// <inheritdoc />
    public Task<Bazaar.Kodo.Probes.Host.DiskReading> ReadDiskAsync(string volume, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volume);
        var drive = new DriveInfo(volume);
        return Task.FromResult(new Bazaar.Kodo.Probes.Host.DiskReading(drive.AvailableFreeSpace, drive.TotalSize));
    }

    /// <inheritdoc />
    public Task<Bazaar.Kodo.Probes.Host.MemoryReading> ReadMemoryAsync(CancellationToken cancellationToken)
    {
        var info = GC.GetGCMemoryInfo();
        return Task.FromResult(new Bazaar.Kodo.Probes.Host.MemoryReading(
            info.TotalAvailableMemoryBytes - info.MemoryLoadBytes,
            info.TotalAvailableMemoryBytes));
    }

    /// <inheritdoc />
    public Task<TimeSpan> ReadClockOffsetAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException("Setup has no reference time source.");
}
