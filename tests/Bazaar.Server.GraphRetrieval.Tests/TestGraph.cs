namespace Bazaar.Server.GraphRetrieval.Tests;

/// <summary>Small helper: build a snapshot from named nodes and look indices up by name.</summary>
public sealed class TestGraph
{
    private readonly GraphSnapshotBuilder _builder = new();
    private GraphSnapshot? _snapshot;

    public GraphSnapshot Snapshot => _snapshot ??= _builder.Build(watermark: 42);

    public TestGraph Node(string id)
    {
        _builder.AddNode(id);
        return this;
    }

    public TestGraph Edge(string from, string to, GraphEdgeKind kind = GraphEdgeKind.Link, double? weight = null)
    {
        _builder.AddEdge(from, to, kind, weight);
        return this;
    }

    public int N(string id) => Snapshot.TryGetNode(id, out int n) ? n : throw new ArgumentException($"No node {id}");

    public string Name(int node) => Snapshot.DocId(node);

    public SeedScore Seed(string id, double score = 1.0) => new(N(id), score);

    public NodeMask Hide(params string[] ids) => NodeMask.AllExcept(Snapshot.NodeCount, ids.Select(N));

    /// <summary>A random graph with a fixed seed: deterministic across runs.</summary>
    public static GraphSnapshot Random(int nodes, int edgesPerNode, int seed)
    {
        var rng = new Random(seed);
        var b = new GraphSnapshotBuilder();
        string Id(int i) => "n" + i.ToString("D7", System.Globalization.CultureInfo.InvariantCulture);
        for (int i = 0; i < nodes; i++)
        {
            b.AddNode(Id(i));
        }

        int kinds = Enum.GetValues<GraphEdgeKind>().Length;
        for (int i = 0; i < nodes; i++)
        {
            for (int k = 0; k < edgesPerNode; k++)
            {
                int j = rng.Next(nodes);
                var kind = (GraphEdgeKind)rng.Next(kinds);
                b.AddEdge(Id(i), Id(j), kind, rng.Next(0, 5) == 0 ? 0.0 : 0.1 + rng.NextDouble());
            }
        }

        return b.Build(watermark: 7);
    }
}
