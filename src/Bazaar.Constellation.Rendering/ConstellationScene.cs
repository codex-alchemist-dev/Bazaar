namespace Bazaar.Constellation.Rendering;

/// <summary>
/// Topology for one graph version: node count, edges as index pairs, and an optional cluster id per node.
/// Edge indices and cluster ids come from the server's graph projection and Louvain pass.
/// </summary>
public sealed class ConstellationScene
{
    public ConstellationScene(int nodeCount, ReadOnlyMemory<int> edgePairs, ReadOnlyMemory<int> clusterIds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(nodeCount);
        if (edgePairs.Length % 2 != 0)
        {
            throw new ArgumentException("Edge list must hold index pairs.", nameof(edgePairs));
        }

        foreach (int index in edgePairs.Span)
        {
            if ((uint)index >= (uint)nodeCount)
            {
                throw new ArgumentOutOfRangeException(nameof(edgePairs), index, "Edge endpoint is outside the node range.");
            }
        }

        if (!clusterIds.IsEmpty && clusterIds.Length != nodeCount)
        {
            throw new ArgumentException("Cluster ids must be empty or hold one id per node.", nameof(clusterIds));
        }

        NodeCount = nodeCount;
        EdgePairs = edgePairs;
        ClusterIds = clusterIds;
    }

    public int NodeCount { get; }

    /// <summary>Packed (from, to) node index pairs.</summary>
    public ReadOnlyMemory<int> EdgePairs { get; }

    /// <summary>Cluster id per node, or empty when clustering has not run.</summary>
    public ReadOnlyMemory<int> ClusterIds { get; }

    public int EdgeCount => EdgePairs.Length / 2;
}
