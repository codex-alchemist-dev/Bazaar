using Bazaar.Contracts.Graph;

namespace Bazaar.Server.Constellation.Tiles;

/// <summary>
/// Supplies the versioned graph that the Constellation layout runs on (doc 19). Implementations read the Vault index or another
/// store; the layout itself is pure. A snapshot carries its own <see cref="GraphSnapshot.GraphVersion"/>.
/// </summary>
public interface IConstellationGraphSource
{
    Task<GraphSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
}
