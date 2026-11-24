namespace Bazaar.Server.GraphRetrieval.Tests;

public sealed class GraphSnapshotTests
{
    [Fact]
    public void Builder_assigns_dense_ids_in_ordinal_order_regardless_of_insertion_order()
    {
        GraphSnapshot a = new GraphSnapshotBuilder().AddEdge("b", "a", GraphEdgeKind.Link).AddEdge("c", "b", GraphEdgeKind.DerivedFrom).Build();
        GraphSnapshot b = new GraphSnapshotBuilder().AddEdge("c", "b", GraphEdgeKind.DerivedFrom).AddEdge("b", "a", GraphEdgeKind.Link).Build();
        Assert.Equal(new[] { "a", "b", "c" }, Enumerable.Range(0, a.NodeCount).Select(a.DocId));
        Assert.Equal(a.OutRowOffsets.ToArray(), b.OutRowOffsets.ToArray());
        Assert.Equal(a.OutTargets.ToArray(), b.OutTargets.ToArray());
        Assert.Equal(a.EdgeKinds.ToArray(), b.EdgeKinds.ToArray());
    }

    [Fact]
    public void Builder_dedupes_edges_keeping_the_largest_weight()
    {
        GraphSnapshot s = new GraphSnapshotBuilder()
            .AddEdge("a", "b", GraphEdgeKind.Link, 0.2)
            .AddEdge("a", "b", GraphEdgeKind.Link, 0.7)
            .AddEdge("a", "b", GraphEdgeKind.Embed)
            .Build();
        Assert.Equal(2, s.EdgeCount);
        Assert.Equal(0.7f, s.EdgeWeights.Span[0]);
    }

    [Fact]
    public void Builder_fills_default_weights_and_rejects_bad_ones()
    {
        GraphSnapshot s = new GraphSnapshotBuilder().AddEdge("a", "b", GraphEdgeKind.DerivedFrom).Build(watermark: 9);
        Assert.Equal(0.8f, s.EdgeWeights.Span[0]);
        Assert.Equal(9, s.Watermark);
        Assert.Throws<ArgumentOutOfRangeException>(() => new GraphSnapshotBuilder().AddEdge("a", "b", GraphEdgeKind.Link, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GraphSnapshotBuilder().AddEdge("a", "b", GraphEdgeKind.Link, double.NaN));
    }

    [Fact]
    public void Reverse_csr_lists_incoming_edges()
    {
        GraphSnapshot s = new GraphSnapshotBuilder().AddEdge("a", "c", GraphEdgeKind.Link).AddEdge("b", "c", GraphEdgeKind.Supersedes).Build();
        int c = 2;
        Assert.Equal(2, s.InRowOffsets.Span[c + 1] - s.InRowOffsets.Span[c]);
        Assert.Equal(new[] { 0, 1 }, s.InSources.Span[s.InRowOffsets.Span[c]..s.InRowOffsets.Span[c + 1]].ToArray());
    }

    [Fact]
    public void Constructor_validates_the_csr()
    {
        Assert.Throws<ArgumentException>(() => new GraphSnapshot(["a"], [0, 1, 1], [0], [0], [1f]));
        Assert.Throws<ArgumentException>(() => new GraphSnapshot(["a"], [0, 1], [5], [0], [1f]));
        Assert.Throws<ArgumentException>(() => new GraphSnapshot(["a"], [0, 1], [0], [40], [1f]));
        Assert.Throws<ArgumentException>(() => new GraphSnapshot(["a"], [0, 1], [0], [0], [-1f]));
        Assert.Throws<ArgumentException>(() => new GraphSnapshot(["a", "a"], [0, 0, 0], [], [], []));
        Assert.Throws<ArgumentException>(() => new GraphSnapshot(["a", "b"], [0, 1, 0], [0], [0], [1f]));
        var empty = new GraphSnapshot([], [0], [], [], []);
        Assert.Equal(0, empty.NodeCount);
    }

    [Fact]
    public void Node_mask_and_edge_kind_set_behave()
    {
        NodeMask m = NodeMask.FromNodes(70, [0, 64, 69]);
        Assert.True(m.Contains(64));
        Assert.False(m.Contains(1));
        Assert.False(m.Contains(70));
        Assert.False(m.Contains(-1));
        Assert.True(NodeMask.All(70).And(m).Contains(69));
        Assert.False(NodeMask.AllExcept(70, [3]).Contains(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => NodeMask.FromNodes(5, [5]));
        Assert.Throws<ArgumentException>(() => m.And(NodeMask.All(3)));

        EdgeKindSet dag = EdgeKindSet.Dag;
        Assert.True(dag.Contains(GraphEdgeKind.Supersedes));
        Assert.False(dag.Contains(GraphEdgeKind.Link));
        Assert.True(EdgeKindSet.None.IsEmpty);
        Assert.True(EdgeKindSet.All.Contains(31));
    }
}
