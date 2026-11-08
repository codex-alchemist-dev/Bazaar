namespace Bazaar.Contracts.Graph;

/// <summary>One cluster glyph of a level-of-detail feed: a community drawn at the centroid of its members (doc 19).</summary>
public sealed record GraphLodGlyph(int Cluster, float[] Centroid, int Count);

/// <summary>Edges between two clusters, weighted by the number of graph edges that cross them.</summary>
public sealed record GraphLodEdge(int A, int B, int Weight);

/// <summary>Node indices inside one cell of the tile grid. Cells are row-major and start at 0.</summary>
public sealed record GraphLodTile(int Col, int Row, int[] NodeIndices);

/// <summary>
/// The level-of-detail feed for one graph version, sent on <c>graph:&lt;id&gt;</c>. When <see cref="ClustersOnly"/> is true,
/// <see cref="Tiles"/> is empty (the graph is above the node cap, <c>constellation.node_cap</c>). The renderer drops any
/// frame whose <see cref="GraphVersion"/> is older than the newest one it applied.
/// </summary>
public sealed record GraphLodFrame(
    string S,
    long GraphVersion,
    bool ClustersOnly,
    IReadOnlyList<GraphLodGlyph> Clusters,
    IReadOnlyList<GraphLodEdge> ClusterEdges,
    IReadOnlyList<GraphLodTile> Tiles);
