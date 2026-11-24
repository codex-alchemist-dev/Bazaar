using Bazaar.Contracts.Graph;
using Bazaar.Server.Constellation.Lod;
using Bazaar.Server.Constellation.Tiles;

namespace Bazaar.Server.Tests.Constellation.Lod;

public class ConstellationGraphLodSourceTests
{
    private static readonly Guid _graph = Guid.Parse("00000000-0000-0000-0000-0000000000e1");
    private static readonly Guid _reader = Guid.Parse("00000000-0000-0000-0000-0000000000f1");
    private static readonly Guid _stranger = Guid.Parse("00000000-0000-0000-0000-0000000000f2");
    private static readonly string[] _nodeIds = ["a", "b", "c", "d", "e", "f"];

    /// <summary>Two triangles joined by one bridge edge: Louvain should split them into two communities.</summary>
    private static GraphSnapshot TwoTriangles(long version = 3) => new(
        version,
        _nodeIds.Select(id => new GraphNode(id)).ToArray(),
        [
            new GraphEdge("a", "b"), new GraphEdge("b", "c"), new GraphEdge("c", "a"),
            new GraphEdge("d", "e"), new GraphEdge("e", "f"), new GraphEdge("f", "d"),
            new GraphEdge("c", "d"),
        ]);

    private static ConstellationGraphLodSource Source(GraphSnapshot snapshot, int nodeCap = 1000) =>
        new(new FixedGraph(snapshot), new AllowOnly(_reader, _graph), _graph, nodeCap);

    [Fact]
    public async Task A_reader_gets_the_feed_for_the_shared_snapshot_version()
    {
        var source = Source(TwoTriangles(version: 9));

        var frame = await source.GetAsync(_reader, _graph, TestContext.Current.CancellationToken);

        Assert.NotNull(frame);
        Assert.Equal(9, frame.GraphVersion);
        Assert.False(frame.ClustersOnly);
    }

    [Fact]
    public async Task Two_triangles_with_one_bridge_form_two_clusters_with_one_crossing_edge()
    {
        var source = Source(TwoTriangles());

        var frame = await source.GetAsync(_reader, _graph, TestContext.Current.CancellationToken);

        Assert.NotNull(frame);
        Assert.Equal(2, frame.Clusters.Count);
        Assert.Equal(6, frame.Clusters.Sum(c => c.Count));
        var edge = Assert.Single(frame.ClusterEdges);
        Assert.Equal(1, edge.Weight);
    }

    [Fact]
    public async Task A_principal_without_access_is_denied_without_a_frame()
    {
        var source = Source(TwoTriangles());

        var frame = await source.GetAsync(_stranger, _graph, TestContext.Current.CancellationToken);

        Assert.Null(frame);
    }

    [Fact]
    public async Task Another_graph_id_is_denied_even_for_a_reader()
    {
        var source = Source(TwoTriangles());

        var frame = await source.GetAsync(_reader, Guid.NewGuid(), TestContext.Current.CancellationToken);

        Assert.Null(frame);
    }

    [Fact]
    public async Task An_empty_graph_gives_an_empty_frame_at_its_version()
    {
        var source = Source(new GraphSnapshot(0, [], []));

        var frame = await source.GetAsync(_reader, _graph, TestContext.Current.CancellationToken);

        Assert.NotNull(frame);
        Assert.Empty(frame.Clusters);
        Assert.Empty(frame.ClusterEdges);
        Assert.Empty(frame.Tiles);
        Assert.Equal(0, frame.GraphVersion);
    }

    [Fact]
    public async Task Above_the_node_cap_the_feed_is_clusters_only_with_no_tiles()
    {
        var source = Source(TwoTriangles(), nodeCap: 4);

        var frame = await source.GetAsync(_reader, _graph, TestContext.Current.CancellationToken);

        Assert.NotNull(frame);
        Assert.True(frame.ClustersOnly);
        Assert.Empty(frame.Tiles);
        Assert.NotEmpty(frame.Clusters);
    }

    [Fact]
    public async Task The_same_snapshot_gives_the_same_feed()
    {
        var first = await Source(TwoTriangles()).GetAsync(_reader, _graph, TestContext.Current.CancellationToken);
        var second = await Source(TwoTriangles()).GetAsync(_reader, _graph, TestContext.Current.CancellationToken);

        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(first), System.Text.Json.JsonSerializer.Serialize(second));
    }

    [Fact]
    public void A_non_positive_node_cap_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ConstellationGraphLodSource(new FixedGraph(TwoTriangles()), new AllowOnly(_reader, _graph), _graph, 0));
    }

    private sealed class FixedGraph(GraphSnapshot snapshot) : IConstellationGraphSource
    {
        public Task<GraphSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(snapshot);
    }

    private sealed class AllowOnly(Guid principal, Guid graph) : IConstellationAccessPolicy
    {
        public ValueTask<bool> CanReadAsync(Guid principalId, Guid graphId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(principalId == principal && graphId == graph);
    }
}
