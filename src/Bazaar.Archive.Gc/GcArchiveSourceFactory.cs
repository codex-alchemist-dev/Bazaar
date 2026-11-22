using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc.Read;

namespace Bazaar.Archive.Gc;

/// <summary>Builds the GC and project sources for one owner and routes by unit kind.</summary>
public sealed class GcArchiveSourceFactory(IGcLogReader reader, IGcArchiveBridge bridge, IProjectGcLister lister) : IArchiveSourceFactory
{
    public IArchiveSource CreateFor(Guid ownerId)
    {
        var gc = new GcArchiveSource(reader, bridge, new GcPrincipal(ownerId, PrincipalKind.Owner, "owner"));
        return new RoutingArchiveSource(gc, new ProjectArchiveSource(lister, gc));
    }
}

/// <summary>Sends a unit to the GC source or the project source by its kind.</summary>
public sealed class RoutingArchiveSource(IArchiveSource gc, IArchiveSource project) : IArchiveSource
{
    private IArchiveSource For(ArchiveUnit unit) => unit.Kind == ArchiveUnitKind.GroupChat ? gc : project;

    public IAsyncEnumerable<ArchiveRecord> ReadAsync(ArchiveUnit unit, CancellationToken ct) => For(unit).ReadAsync(unit, ct);

    public Task MarkArchivedAsync(ArchiveUnit unit, string rootHash, CancellationToken ct) => For(unit).MarkArchivedAsync(unit, rootHash, ct);
}
