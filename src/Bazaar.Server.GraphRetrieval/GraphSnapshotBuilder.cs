namespace Bazaar.Server.GraphRetrieval;

/// <summary>
/// Builds a <see cref="GraphSnapshot"/> from document ids and typed edges. Dense node ids follow the ordinal order of the
/// document ids, and edges are sorted and de-duplicated per (source, target, kind) keeping the largest weight, so the same
/// graph always yields the same snapshot whatever the insertion order.
/// </summary>
public sealed class GraphSnapshotBuilder
{
    private readonly HashSet<string> _nodes = new(StringComparer.Ordinal);
    private readonly List<(string Src, string Dst, GraphEdgeKind Kind, float Weight)> _edges = [];

    public int NodeCount => _nodes.Count;

    /// <summary>Adds a node (also added implicitly by <see cref="AddEdge"/>).</summary>
    public GraphSnapshotBuilder AddNode(string docId)
    {
        ArgumentException.ThrowIfNullOrEmpty(docId);
        _nodes.Add(docId);
        return this;
    }

    /// <summary>Adds a typed edge; the weight defaults to <see cref="GraphEdgeWeights.Default"/>.</summary>
    public GraphSnapshotBuilder AddEdge(string source, string target, GraphEdgeKind kind, double? weight = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        ArgumentException.ThrowIfNullOrEmpty(target);
        double w = weight ?? GraphEdgeWeights.Default(kind);
        if (!double.IsFinite(w) || w < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(weight), "Weight must be finite and non-negative.");
        }

        _nodes.Add(source);
        _nodes.Add(target);
        _edges.Add((source, target, kind, (float)w));
        return this;
    }

    public GraphSnapshot Build(long watermark = 0)
    {
        string[] ids = [.. _nodes];
        Array.Sort(ids, StringComparer.Ordinal);
        var index = new Dictionary<string, int>(ids.Length, StringComparer.Ordinal);
        for (int i = 0; i < ids.Length; i++)
        {
            index[ids[i]] = i;
        }

        var edges = new (int Src, int Dst, byte Kind, float Weight)[_edges.Count];
        for (int i = 0; i < edges.Length; i++)
        {
            (string s, string d, GraphEdgeKind k, float w) = _edges[i];
            edges[i] = (index[s], index[d], (byte)k, w);
        }

        Array.Sort(edges, static (a, b) =>
        {
            int c = a.Src.CompareTo(b.Src);
            if (c != 0)
            {
                return c;
            }

            c = a.Dst.CompareTo(b.Dst);
            if (c != 0)
            {
                return c;
            }

            c = a.Kind.CompareTo(b.Kind);
            return c != 0 ? c : b.Weight.CompareTo(a.Weight); // largest weight first, so the first duplicate wins
        });

        var rows = new int[ids.Length + 1];
        var cols = new List<int>(edges.Length);
        var kinds = new List<byte>(edges.Length);
        var weights = new List<float>(edges.Length);
        (int Src, int Dst, byte Kind)? last = null;
        foreach ((int src, int dst, byte kind, float weight) in edges)
        {
            if (last is { } l && l.Src == src && l.Dst == dst && l.Kind == kind)
            {
                continue;
            }

            last = (src, dst, kind);
            rows[src + 1]++;
            cols.Add(dst);
            kinds.Add(kind);
            weights.Add(weight);
        }

        for (int i = 0; i < ids.Length; i++)
        {
            rows[i + 1] += rows[i];
        }

        return new GraphSnapshot(ids, rows, cols, kinds, weights, watermark);
    }
}
