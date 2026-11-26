using Bazaar.Server.Kodo;
using Bazaar.Server.WebAdmin;

namespace Bazaar.Server.OpsPorts;

/// <summary>
/// The doc 51 status port for a process that is not the server: it reads the file the server host writes
/// (<see cref="AdminStatusFile"/>). A missing file throws, so the screen reads "unavailable"; a stale file reads as "server not
/// reporting" (see <see cref="AdminStatusFile.Read"/>).
/// </summary>
public sealed class FileAdminStatusSource(string path, TimeProvider time, TimeSpan? staleAfter = null) : IAdminStatusSource
{
    public Task<AdminStatusView> GetAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(AdminStatusFile.Read(path, time.GetUtcNow(), staleAfter ?? AdminStatusFile.DefaultStaleAfter));
    }
}

/// <summary>
/// The doc 51 Kodo port for a process that is not the server: it reads the latest snapshot Kodo writes
/// (<see cref="KodoSnapshotFile"/>) and maps it with <see cref="KodoAdminViews"/>, the mapping the web panel uses. A missing file
/// reads as Kodo not having reported; a snapshot older than the silence budget reads as silent, never as green.
/// </summary>
public sealed class FileAdminKodoSource(string path, TimeProvider time, TimeSpan? staleAfter = null) : IAdminKodoSource
{
    public Task<AdminKodoView> GetAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var snapshot = KodoSnapshotFile.TryRead(path);
        return Task.FromResult(snapshot is null
            ? KodoAdminViews.NotReady()
            : KodoAdminViews.FromSnapshot(snapshot, time.GetUtcNow(), staleAfter ?? KodoAdminViews.DefaultStaleAfter));
    }
}
