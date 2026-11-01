namespace Bazaar.Contracts.Graph;

/// <summary>
/// One node of the knowledge graph the Constellation shows (doc 19). The id is the graph-scoped node key.
/// </summary>
public sealed record GraphNode(string Id);

/// <summary>A directed link between two nodes, by node id. Endpoints are not checked here; the projection decides which links it keeps.</summary>
public sealed record GraphEdge(string From, string To);

/// <summary>A versioned copy of the graph that the layout engine lays out.</summary>
public sealed record GraphSnapshot(
    long GraphVersion,
    IReadOnlyList<GraphNode> Nodes,
    IReadOnlyList<GraphEdge> Edges);

/// <summary>
/// One layout result sent to clients. <c>Xyz</c> is packed x,y,z per node index (stride 3), in the node order of the
/// snapshot it was computed from. 2D layouts keep z at 0. Clients drop any frame older than the newest one applied (doc 19, ordering).
/// </summary>
public sealed record LayoutFrame(long GraphVersion, long LayoutSeq, string FocusId, ReadOnlyMemory<float> Xyz);
