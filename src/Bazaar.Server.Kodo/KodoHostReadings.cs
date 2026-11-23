using Bazaar.Kodo.Probes.Host;
using Bazaar.Kodo.Probes.Self;

namespace Bazaar.Server.Kodo;

/// <summary>
/// The default operating-system reads for the host probes, from portable .NET APIs only (no OS-specific API outside a provider,
/// CLAUDE.md). A host with a better source registers its own <see cref="IHostReadings"/> first. The clock offset has no source here,
/// so it throws and the probe says unknown; <see cref="KodoServerOptions.IncludeClockDrift"/> keeps it off by default.
/// </summary>
internal sealed class KodoHostReadings(KodoServerOptions options) : IHostReadings
{
    public Task<DiskReading> ReadDiskAsync(string volume, CancellationToken cancellationToken)
    {
        string root = Path.GetPathRoot(Path.GetFullPath(options.DataVolumePath)) ?? throw new IOException("The data path has no volume.");
        DriveInfo drive = new(root);
        return Task.FromResult(new DiskReading(drive.AvailableFreeSpace, drive.TotalSize));
    }

    public Task<MemoryReading> ReadMemoryAsync(CancellationToken cancellationToken)
    {
        GCMemoryInfo info = GC.GetGCMemoryInfo();
        long total = info.TotalAvailableMemoryBytes;
        long available = Math.Max(0, total - info.MemoryLoadBytes);
        return Task.FromResult(new MemoryReading(available, total));
    }

    public Task<TimeSpan> ReadClockOffsetAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException("No clock reference source is configured.");
}

/// <summary>The self readings: the journal writer's own state. There is no unsent spill backlog because nothing is sent to a store yet.</summary>
internal sealed class KodoSelfReadings(KodoJournal journal) : IKodoSelfReadings
{
    public Task<bool> CanWriteJournalAsync(CancellationToken cancellationToken) => Task.FromResult(journal.CheckWritable());

    public Task<SpillFileReading?> ReadSpillFileAsync(CancellationToken cancellationToken) => Task.FromResult<SpillFileReading?>(null);
}
