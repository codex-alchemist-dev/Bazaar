namespace Bazaar.Constellation.Rendering.Core;

/// <summary>
/// Topology a backend draws for one graph version: node count and edges as index pairs.
/// It carries no cluster or other domain data; per-node looks come through <see cref="NodeStyleTable"/>.
/// </summary>
public sealed class RenderScene
{
    public RenderScene(int nodeCount, ReadOnlyMemory<int> edgePairs)
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

        NodeCount = nodeCount;
        EdgePairs = edgePairs;
    }

    public int NodeCount { get; }

    /// <summary>Packed (from, to) node index pairs.</summary>
    public ReadOnlyMemory<int> EdgePairs { get; }

    public int EdgeCount => EdgePairs.Length / 2;
}
