namespace Bazaar.Constellation.Communities.Detection;

/// <summary>Louvain output: a 0-based community index per node id, and the modularity of that partition.</summary>
internal sealed record LouvainResult(IReadOnlyDictionary<string, int> CommunityByNode, double Modularity);
