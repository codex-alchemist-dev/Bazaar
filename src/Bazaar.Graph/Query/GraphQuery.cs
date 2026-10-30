using Bazaar.Graph.Model;

namespace Bazaar.Graph.Query;

/// <summary>Read-only questions over a built graph. Each answer is small, so an agent can ask instead of reading whole files.</summary>
public sealed class GraphQuery(KnowledgeGraph graph)
{
    public const int MaxDepth = 3;

    private readonly Dictionary<string, GraphNode> _nodes = graph.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
    private readonly Dictionary<string, List<GraphEdge>> _outgoing = Group(graph.Edges, e => e.Source);
    private readonly Dictionary<string, List<GraphEdge>> _incoming = Group(graph.Edges, e => e.Target);

    public IReadOnlyList<GraphNode> Search(string text, int limit = 20)
    {
        var needle = text.Trim();
        return graph.Nodes
            .Where(n => n.Label.Contains(needle, StringComparison.OrdinalIgnoreCase) || n.Id.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n.Label.Equals(needle, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(n => n.Id, StringComparer.Ordinal)
            .Take(limit)
            .ToList();
    }

    /// <summary>Resolves an exact id first, then an exact label. Returns the candidates when a label is ambiguous.</summary>
    public (GraphNode? Node, IReadOnlyList<GraphNode> Candidates) Resolve(string idOrLabel)
    {
        if (_nodes.TryGetValue(idOrLabel, out var byId))
        {
            return (byId, [byId]);
        }

        var matches = graph.Nodes.Where(n => n.Label.Equals(idOrLabel, StringComparison.Ordinal)).ToList();

        // A file and a type can share a label (Store.cs and class Store). Prefer the symbol: that is what an agent usually means.
        var symbols = matches.Where(n => n.Kind != NodeKind.File).ToList();
        if (symbols.Count > 0)
        {
            matches = symbols;
        }

        return matches.Count == 1 ? (matches[0], matches) : (null, matches);
    }

    /// <summary>Edges within <paramref name="depth"/> hops of the node, followed in both directions.</summary>
    public IReadOnlyList<GraphEdge> Neighbors(string nodeId, int depth)
    {
        var limit = Math.Clamp(depth, 1, MaxDepth);
        var seen = new HashSet<string> { nodeId };
        var frontier = new List<string> { nodeId };
        var result = new List<GraphEdge>();
        var edgeSeen = new HashSet<GraphEdge>();

        for (var hop = 0; hop < limit && frontier.Count > 0; hop++)
        {
            var next = new List<string>();
            foreach (var current in frontier)
            {
                foreach (var edge in Edges(current))
                {
                    if (edgeSeen.Add(edge))
                    {
                        result.Add(edge);
                    }

                    var other = edge.Source == current ? edge.Target : edge.Source;
                    if (seen.Add(other))
                    {
                        next.Add(other);
                    }
                }
            }

            frontier = next;
        }

        return result;
    }

    /// <summary>Highest-degree nodes. File and section nodes are excluded: they only show structure, so they swamp the report.</summary>
    public IReadOnlyList<(GraphNode Node, int Degree)> GodNodes(int top = 10)
    {
        var degree = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var edge in graph.Edges)
        {
            degree[edge.Source] = degree.GetValueOrDefault(edge.Source) + 1;
            degree[edge.Target] = degree.GetValueOrDefault(edge.Target) + 1;
        }

        return degree
            .Where(pair => _nodes.TryGetValue(pair.Key, out var node) && node.Kind is NodeKind.Type or NodeKind.Member or NodeKind.External)
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Take(top)
            .Select(pair => (_nodes[pair.Key], pair.Value))
            .ToList();
    }

    public GraphNode? Node(string id) => _nodes.GetValueOrDefault(id);

    private IEnumerable<GraphEdge> Edges(string id)
    {
        if (_outgoing.TryGetValue(id, out var outs))
        {
            foreach (var edge in outs)
            {
                yield return edge;
            }
        }

        if (_incoming.TryGetValue(id, out var ins))
        {
            foreach (var edge in ins)
            {
                yield return edge;
            }
        }
    }

    private static Dictionary<string, List<GraphEdge>> Group(IEnumerable<GraphEdge> edges, Func<GraphEdge, string> key)
    {
        var map = new Dictionary<string, List<GraphEdge>>(StringComparer.Ordinal);
        foreach (var edge in edges)
        {
            var k = key(edge);
            if (!map.TryGetValue(k, out var list))
            {
                map[k] = list = [];
            }

            list.Add(edge);
        }

        return map;
    }
}
