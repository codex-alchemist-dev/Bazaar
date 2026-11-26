namespace Bazaar.Server.GraphRetrieval;

/// <summary>A node reached by expansion. <see cref="Level"/> is the hop count of its best path; seeds are level 0 with predecessor -1.</summary>
public readonly record struct ReachedNode(int Node, int Level, double Score, int Predecessor, GraphEdgeKind PredecessorKind);

/// <summary>A path from a seed or root to a node: <c>Nodes[0]</c> is the origin, <c>Kinds[i]</c> the edge from <c>Nodes[i]</c> to <c>Nodes[i+1]</c>.</summary>
public sealed record GraphPath(IReadOnlyList<int> Nodes, IReadOnlyList<GraphEdgeKind> Kinds);

/// <summary>
/// Expansion output. <see cref="Nodes"/> holds the final best entry per node ordered by level, then score descending, then node.
/// The full improvement history is kept so <see cref="TracePath"/> returns the exact path that produced a score.
/// </summary>
public sealed class ExpansionResult
{
    private readonly ReachedNode[] _history;
    private readonly Dictionary<int, ReachedNode> _final;

    public ExpansionResult(IReadOnlyList<ReachedNode> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        _history = [.. history];
        _final = [];
        foreach (ReachedNode h in _history)
        {
            _final[h.Node] = h; // history is in round order, so the last entry per node wins
        }

        ReachedNode[] nodes = [.. _final.Values];
        Array.Sort(nodes, static (a, b) =>
        {
            int c = a.Level.CompareTo(b.Level);
            if (c != 0)
            {
                return c;
            }

            c = b.Score.CompareTo(a.Score);
            return c != 0 ? c : a.Node.CompareTo(b.Node);
        });
        Nodes = nodes;
    }

    public IReadOnlyList<ReachedNode> Nodes { get; }

    public bool TryGet(int node, out ReachedNode reached) => _final.TryGetValue(node, out reached);

    /// <summary>The path from the seed that produced the node's final score, or null if the node was not reached.</summary>
    public GraphPath? TracePath(int node)
    {
        if (!_final.TryGetValue(node, out ReachedNode cur))
        {
            return null;
        }

        var nodes = new List<int> { node };
        var kinds = new List<GraphEdgeKind>();
        int round = cur.Level;
        while (cur.Predecessor >= 0)
        {
            kinds.Add(cur.PredecessorKind);
            int p = cur.Predecessor;
            int before = round - 1;

            // The predecessor's state as of the round before this entry: its latest entry with Level <= before.
            ReachedNode? found = null;
            for (int i = _history.Length - 1; i >= 0; i--)
            {
                if (_history[i].Node == p && _history[i].Level <= before)
                {
                    found = _history[i];
                    break;
                }
            }

            if (found is null)
            {
                return null;
            }

            nodes.Add(p);
            cur = found.Value;
            round = cur.Level;
        }

        nodes.Reverse();
        kinds.Reverse();
        return new GraphPath(nodes, kinds);
    }
}

/// <summary>A node in a closure, with its depth from the nearest root.</summary>
public readonly record struct ClosureNode(int Node, int Depth, int Predecessor, GraphEdgeKind PredecessorKind);

/// <summary>Closure output: nodes ordered by depth then node id (roots at depth 0), plus every node that sits on a cycle.</summary>
public sealed record ClosureResult(IReadOnlyList<ClosureNode> Nodes, IReadOnlyList<int> CycleNodes);

/// <summary>A node with a score.</summary>
public readonly record struct NodeScore(int Node, double Score);

/// <summary>PageRank output, highest first (ties by node id).</summary>
public sealed record NodeScores(IReadOnlyList<NodeScore> Scores, int Iterations, bool Converged, double FinalDelta);

/// <summary>Where a subgraph node came from.</summary>
[Flags]
public enum NodeOrigin
{
    None = 0,
    Seed = 1,
    Expansion = 2,
    Closure = 4,
}

/// <summary>A ranked node of an assembled subgraph. Rank 0 is the best.</summary>
public readonly record struct SubgraphNode(int Node, double Score, int Rank, NodeOrigin Origin, int Level);

/// <summary>The path that justifies a non-seed node's presence.</summary>
public sealed record SubgraphPath(int Node, GraphPath Path);

/// <summary>Assembled subgraph: ranked nodes, justifying paths, and any DAG cycle nodes found by closure.</summary>
public sealed record SubgraphResult(
    IReadOnlyList<SubgraphNode> Nodes,
    IReadOnlyList<SubgraphPath> Paths,
    IReadOnlyList<int> CycleNodes,
    RankMethod Method,
    int PprIterations,
    bool PprConverged);
