using Bazaar.Contracts.Graph;
using Bazaar.Server.Constellation.Tiles;

namespace Bazaar.Server.Tests.Constellation;

public sealed class LayoutTileSourceTests
{
    private sealed class FixedGraph(GraphSnapshot snapshot) : IConstellationGraphSource
    {
        public GraphSnapshot Snapshot { get; set; } = snapshot;

        public Task<GraphSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) => Task.FromResult(Snapshot);
    }

    private static GraphSnapshot Chain(long version, int count) =>
        new(version,
            Enumerable.Range(0, count).Select(i => new GraphNode($"n{i}")).ToArray(),
            Enumerable.Range(0, count - 1).Select(i => new GraphEdge($"n{i}", $"n{i + 1}")).ToArray());

    private static ViewportRequest Everything(long version, int maxNodes) =>
        new("view-1", version, ZoomTier.Far, -1e6f, -1e6f, -1e6f, 1e6f, 1e6f, 1e6f, maxNodes);

    [Fact]
    public async Task Empty_graph_gives_an_empty_frame_at_version_zero()
    {
        var source = new LayoutTileSource(new ConstellationEmptyGraphSource());

        var frame = await source.GetFrameAsync(Everything(0, 10), TestContext.Current.CancellationToken);

        Assert.NotNull(frame);
        Assert.Equal(0L, frame.GraphVersion);
        Assert.Empty(frame.NodeIds);
        Assert.Equal(0, frame.Xyz.Length);
    }

    [Fact]
    public async Task Frame_holds_every_node_inside_the_box_with_finite_positions()
    {
        var source = new LayoutTileSource(new FixedGraph(Chain(3, 12)));

        var frame = await source.GetFrameAsync(Everything(3, 100), TestContext.Current.CancellationToken);

        Assert.NotNull(frame);
        Assert.Equal(12, frame.NodeIds.Count);
        Assert.Equal(36, frame.Xyz.Length);
        Assert.All(frame.Xyz.ToArray(), value => Assert.True(float.IsFinite(value)));
    }

    [Fact]
    public async Task Node_cap_limits_the_frame()
    {
        var source = new LayoutTileSource(new FixedGraph(Chain(1, 12)));

        var frame = await source.GetFrameAsync(Everything(1, 4), TestContext.Current.CancellationToken);

        Assert.NotNull(frame);
        Assert.Equal(4, frame.NodeIds.Count);
    }

    [Fact]
    public async Task Box_outside_the_layout_gives_no_nodes()
    {
        var source = new LayoutTileSource(new FixedGraph(Chain(1, 5)));
        var request = new ViewportRequest("view-1", 1, ZoomTier.Near, 1e5f, 1e5f, -1e6f, 1e6f, 1e6f, 1e6f, 10);

        var frame = await source.GetFrameAsync(request, TestContext.Current.CancellationToken);

        Assert.NotNull(frame);
        Assert.Empty(frame.NodeIds);
    }

    [Fact]
    public async Task Layout_is_deterministic_for_one_graph_version()
    {
        var graph = new FixedGraph(Chain(7, 20));
        var source = new LayoutTileSource(graph);

        var first = await source.GetFrameAsync(Everything(7, 100), TestContext.Current.CancellationToken);
        var second = await source.GetFrameAsync(Everything(7, 100), TestContext.Current.CancellationToken);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first.NodeIds, second.NodeIds);
        Assert.Equal(first.Xyz.ToArray(), second.Xyz.ToArray());
    }

    [Fact]
    public async Task A_new_graph_version_is_reported_on_the_frame()
    {
        var graph = new FixedGraph(Chain(1, 4));
        var source = new LayoutTileSource(graph);
        await source.GetFrameAsync(Everything(1, 10), TestContext.Current.CancellationToken);

        graph.Snapshot = Chain(2, 6);
        var frame = await source.GetFrameAsync(Everything(2, 10), TestContext.Current.CancellationToken);

        Assert.NotNull(frame);
        Assert.Equal(2L, frame.GraphVersion);
        Assert.Equal(6, frame.NodeIds.Count);
    }
}
