using Bazaar.Contracts.Graph;

namespace Bazaar.Server.Constellation.Layout;

/// <summary>An undirected spring between two node indices of a <see cref="LayoutGraph"/>.</summary>
public readonly record struct LayoutEdge(int Source, int Target);

/// <summary>
/// The layout's view of a graph: node ids in index order and edges by index. Index order is the order used for
/// packed positions. Layout never reads ids; they are kept so callers can map positions back to nodes.
/// </summary>
public sealed class LayoutGraph
{
    public LayoutGraph(IReadOnlyList<string> nodeIds, IReadOnlyList<LayoutEdge> edges)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);
        ArgumentNullException.ThrowIfNull(edges);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in nodeIds)
        {
            ArgumentNullException.ThrowIfNull(id);
            if (!seen.Add(id))
            {
                throw new ArgumentException($"Duplicate node id '{id}'.", nameof(nodeIds));
            }
        }

        foreach (var edge in edges)
        {
            if ((uint)edge.Source >= (uint)nodeIds.Count || (uint)edge.Target >= (uint)nodeIds.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(edges), $"Edge ({edge.Source}, {edge.Target}) is outside the node range.");
            }
        }

        NodeIds = nodeIds.ToArray();
        Edges = edges.ToArray();
    }

    public IReadOnlyList<string> NodeIds { get; }

    public IReadOnlyList<LayoutEdge> Edges { get; }

    public int NodeCount => NodeIds.Count;

    /// <summary>
    /// Builds the layout view of a snapshot. Node order is the snapshot order. Edges whose endpoint is not a node
    /// of the snapshot are dropped here; the projection decides whether such links are kept.
    /// </summary>
    public static LayoutGraph FromSnapshot(GraphSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var ids = snapshot.Nodes.Select(node => node.Id).ToArray();
        var index = new Dictionary<string, int>(ids.Length, StringComparer.Ordinal);
        for (var i = 0; i < ids.Length; i++)
        {
            if (!index.TryAdd(ids[i], i))
            {
                throw new ArgumentException($"Duplicate node id '{ids[i]}'.", nameof(snapshot));
            }
        }

        var edges = new List<LayoutEdge>(snapshot.Edges.Count);
        foreach (var edge in snapshot.Edges)
        {
            if (index.TryGetValue(edge.From, out var from) && index.TryGetValue(edge.To, out var to))
            {
                edges.Add(new LayoutEdge(from, to));
            }
        }

        return new LayoutGraph(ids, edges);
    }
}
