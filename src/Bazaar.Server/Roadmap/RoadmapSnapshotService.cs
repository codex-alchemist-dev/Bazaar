using Bazaar.Contracts.Graph;
using Bazaar.Contracts.Roadmap;
using Bazaar.Server.Constellation.Tiles;
using Npgsql;

namespace Bazaar.Server.Roadmap;

/// <summary>
/// Reads one Vault's objective roadmap from the index and returns it as the wire snapshot (doc 38, Layer 3). Nothing is stored:
/// every call derives the roadmap from <c>vault_docs</c>, <c>task_links</c> and <c>tasks</c>.
/// </summary>
public sealed class RoadmapSnapshotService(NpgsqlDataSource dataSource)
{
    /// <summary>The data source the roadmap reads from. Exposed so a graph source over the same database can reuse it.</summary>
    public NpgsqlDataSource DataSource { get; } = dataSource;

    public async Task<RoadmapBoardSnapshot> GetSnapshotAsync(Guid vaultId, CancellationToken cancellationToken = default)
    {
        var load = await RoadmapRepository.LoadAsync(DataSource, vaultId, cancellationToken).ConfigureAwait(false);
        var view = RoadmapReadModel.Build(load.Documents, load.StatesByPath);
        return RoadmapSnapshotMapper.ToSnapshot(vaultId, view, load.Errors);
    }
}

/// <summary>
/// The Constellation's graph source for one Vault's roadmap. Not registered by the host yet; it is ready for the Constellation
/// component to choose once the roadmap view is wanted there.
/// </summary>
public sealed class RoadmapGraphSource(RoadmapSnapshotService service, Guid vaultId) : IConstellationGraphSource
{
    public async Task<GraphSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var load = await RoadmapRepository.LoadAsync(service.DataSource, vaultId, cancellationToken).ConfigureAwait(false);
        return RoadmapGraphProjection.Build(RoadmapReadModel.Build(load.Documents, load.StatesByPath));
    }
}
