using Bazaar.Contracts.Graph;

namespace Bazaar.Server.Constellation.Tiles;

/// <summary>
/// Supplies the versioned graph that the Constellation layout runs on (doc 19). Implementations read the Vault index or another
/// store; the layout itself is pure. A snapshot carries its own <see cref="GraphSnapshot.GraphVersion"/>.
/// </summary>
public interface IConstellationGraphSource
{
    /// <summary>The whole graph, as the owner sees it. Never serve it to another viewer.</summary>
    Task<GraphSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The projection one viewer may read: only the nodes they may read and only edges whose both ends they may read (doc 19,
    /// "Visibility"). The default fails closed: the owner gets the whole graph and everyone else an empty one, so a source
    /// that does not know about viewers cannot leak.
    /// </summary>
    Task<GraphSnapshot> GetSnapshotAsync(GraphViewer viewer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        return viewer.IsOwner
            ? GetSnapshotAsync(cancellationToken)
            : Task.FromResult(new GraphSnapshot(0, Array.Empty<GraphNode>(), Array.Empty<GraphEdge>()));
    }
}
