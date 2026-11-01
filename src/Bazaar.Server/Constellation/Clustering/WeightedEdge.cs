namespace Bazaar.Server.Constellation.Clustering;

/// <summary>
/// An undirected weighted edge between two node ids. <c>From == To</c> is a self-loop.
/// Repeated edges between the same pair are summed. A zero weight contributes nothing.
/// </summary>
public sealed record WeightedEdge(string From, string To, double Weight);

/// <summary>
/// Louvain result. <see cref="CommunityByNode"/> maps every input node id to a 0-based community index,
/// numbered in the order each community first appears in sorted node-id order.
/// <see cref="Modularity"/> is the Newman modularity of that partition on the input graph.
/// </summary>
public sealed record CommunityResult(IReadOnlyDictionary<string, int> CommunityByNode, double Modularity);
