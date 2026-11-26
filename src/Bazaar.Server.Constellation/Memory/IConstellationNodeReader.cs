namespace Bazaar.Server.Constellation.Memory;

/// <summary>
/// Reads Constellation doc nodes for graph retrieval (docs/design/constellation-memory-graph.md, "Convergence design"). One call
/// returns the nodes for the given doc ids with their memory attributes and expanding edges. It applies no access rule; the
/// retriever filters every node through <c>IVaultAccess</c>.
/// </summary>
public interface IConstellationNodeReader
{
    /// <summary>The nodes for <paramref name="docIds"/>. Ids with no Vault doc are left out.</summary>
    Task<IReadOnlyList<ConstellationGraphNode>> GetNodesAsync(IReadOnlyCollection<Guid> docIds, CancellationToken ct);
}
