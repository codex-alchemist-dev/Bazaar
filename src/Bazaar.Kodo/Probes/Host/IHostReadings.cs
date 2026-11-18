namespace Bazaar.Kodo.Probes.Host;

/// <summary>Used and total bytes on one volume.</summary>
public readonly record struct DiskReading(long FreeBytes, long TotalBytes);

/// <summary>Available and total physical memory in bytes.</summary>
public readonly record struct MemoryReading(long AvailableBytes, long TotalBytes);

/// <summary>
/// The operating-system reads behind the host probes (doc 46 sections 9 and 9.1). Kodo's core has no real I/O:
/// the server host supplies the implementation per OS behind its provider seam (doc 03), and tests fake it.
/// </summary>
/// <remarks>
/// An implementation throws when it cannot read a value. The probe turns that into <c>unknown</c>, never green.
/// </remarks>
public interface IHostReadings
{
    /// <summary>Free and total bytes on <paramref name="volume"/>.</summary>
    Task<DiskReading> ReadDiskAsync(string volume, CancellationToken cancellationToken);

    /// <summary>Available and total physical memory.</summary>
    Task<MemoryReading> ReadMemoryAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The signed offset of the local clock from the reference source (positive when the local clock is ahead).
    /// </summary>
    Task<TimeSpan> ReadClockOffsetAsync(CancellationToken cancellationToken);
}
