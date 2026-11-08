using Bazaar.Client.Core.Constellation;
using Bazaar.Client.Core.Panes;
using Bazaar.Client.Core.Tests.Panes;
using Bazaar.Contracts.Graph;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Core.Tests.Constellation;

/// <summary>A tile source that records each request and answers only when the test completes the matching response.</summary>
public sealed class FakeTileSource : IConstellationTileSource
{
    private readonly object _gate = new();
    private readonly List<ViewportRequest> _requests = [];
    private readonly List<TaskCompletionSource<TileFrame?>> _responses = [];

    public IReadOnlyList<ViewportRequest> Requests
    {
        get { lock (_gate) return _requests.ToArray(); }
    }

    public IReadOnlyList<TaskCompletionSource<TileFrame?>> Responses
    {
        get { lock (_gate) return _responses.ToArray(); }
    }

    public Task<TileFrame?> GetFrameAsync(ViewportRequest request, CancellationToken cancellationToken = default)
    {
        var response = new TaskCompletionSource<TileFrame?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _requests.Add(request);
            _responses.Add(response);
        }

        return response.Task;
    }
}

public class ConstellationTileViewModelTests
{
    private const int _cap = 500;
    private static readonly ConstellationBox _box = new(-10, -5, 20, 30);

    private static ConstellationPaneViewModel Pane(IConstellationTileSource? source, int cap = _cap) =>
        new(PaneRegistryTests.Descriptor(PaneKinds.Constellation, """{"nodes":[]}"""), source, cap);

    private static GraphLodFrame Lod(long version, bool clustersOnly = false) =>
        new("graph:1", version, clustersOnly, [], [], []);

    private static TileFrame Frame(long graphVersion, long layoutSeq, params string[] ids) => new(
        viewId: PaneKinds.Constellation,
        graphVersion: graphVersion,
        layoutSeq: layoutSeq,
        key: new TileKey(0, 0, 0),
        tier: Bazaar.Contracts.Graph.ZoomTier.Near,
        dimensions: 2,
        nodeIds: ids,
        xyz: ids.SelectMany((_, i) => new float[] { i, i * 2, 0 }).ToArray(),
        clusters: []);

    private static Task Viewport(ConstellationPaneViewModel pane, ConstellationBox box) =>
        pane.SetViewportAsync(box, TestContext.Current.CancellationToken);

    private static async Task UntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    [Fact]
    public async Task Request_carries_the_visible_box_graph_version_view_id_and_node_cap()
    {
        var source = new FakeTileSource();
        var pane = Pane(source);
        pane.ApplyLodSnapshot(Lod(7));

        var sent = Viewport(pane, _box);
        source.Responses[0].SetResult(null);
        await sent;

        var request = Assert.Single(source.Requests);
        Assert.Equal(PaneKinds.Constellation, request.ViewId);
        Assert.Equal(7, request.GraphVersion);
        Assert.Equal(-10, request.MinX);
        Assert.Equal(-5, request.MinY);
        Assert.Equal(20, request.MaxX);
        Assert.Equal(30, request.MaxY);
        Assert.Equal(0, request.MinZ);
        Assert.Equal(0, request.MaxZ);
        Assert.Equal(_cap, request.MaxNodes);
    }

    [Fact]
    public async Task No_request_is_sent_before_a_layout_snapshot_or_when_the_graph_is_above_the_node_cap()
    {
        var source = new FakeTileSource();
        var pane = Pane(source);

        await Viewport(pane, _box);
        Assert.Empty(source.Requests);

        pane.ApplyLodSnapshot(Lod(7, clustersOnly: true));
        await Viewport(pane, _box);
        Assert.Empty(source.Requests);
        Assert.True(pane.ClustersOnly);
    }

    [Fact]
    public async Task Without_a_tile_source_the_pane_sends_nothing_and_shows_no_tiles()
    {
        var pane = Pane(source: null);
        pane.ApplyLodSnapshot(Lod(7));

        await Viewport(pane, _box);

        Assert.Empty(pane.TileNodes);
        Assert.Equal(7, pane.GraphVersion);
    }

    [Fact]
    public async Task Nodes_of_the_response_are_shown_with_their_positions()
    {
        var source = new FakeTileSource();
        var pane = Pane(source);
        pane.ApplyLodSnapshot(Lod(7));

        var sent = Viewport(pane, _box);
        source.Responses[0].SetResult(Frame(7, 1, "a", "b"));
        await sent;

        Assert.Equal(["a", "b"], pane.TileNodes.Select(n => n.Id));
        Assert.Equal(new TileNode("b", 1, 2), pane.TileNodes[1]);
        Assert.True(pane.HasData);
    }

    [Fact]
    public async Task Response_from_an_older_graph_version_is_dropped_and_a_newer_snapshot_clears_tiles()
    {
        var source = new FakeTileSource();
        var pane = Pane(source);
        pane.ApplyLodSnapshot(Lod(7));
        var sent = Viewport(pane, _box);

        // The graph moves on while the version 7 request is in flight.
        Assert.True(pane.ApplyLodSnapshot(Lod(8)));
        source.Responses[0].SetResult(Frame(7, 1, "a"));
        await sent;

        Assert.Empty(pane.TileNodes);
        Assert.Equal(8, pane.GraphVersion);
    }

    [Fact]
    public async Task Response_whose_own_graph_version_does_not_match_is_dropped()
    {
        var source = new FakeTileSource();
        var pane = Pane(source);
        pane.ApplyLodSnapshot(Lod(7));

        var sent = Viewport(pane, _box);
        source.Responses[0].SetResult(Frame(6, 1, "a"));
        await sent;

        Assert.Empty(pane.TileNodes);
    }

    [Fact]
    public void Older_layout_snapshot_is_ignored()
    {
        var pane = Pane(new FakeTileSource());
        Assert.True(pane.ApplyLodSnapshot(Lod(7)));

        Assert.False(pane.ApplyLodSnapshot(Lod(5)));
        Assert.Equal(7, pane.GraphVersion);
    }

    [Fact]
    public async Task Frame_over_the_node_cap_is_dropped_and_one_within_the_cap_is_shown()
    {
        var source = new FakeTileSource();
        var pane = Pane(source, cap: 2);
        pane.ApplyLodSnapshot(Lod(7));

        var first = Viewport(pane, _box);
        source.Responses[0].SetResult(Frame(7, 1, "a", "b", "c"));
        await first;

        Assert.Empty(pane.TileNodes);
        Assert.Equal(2, source.Requests[0].MaxNodes);

        var second = Viewport(pane, _box);
        source.Responses[1].SetResult(Frame(7, 2, "a", "b"));
        await second;

        Assert.Equal(["a", "b"], pane.TileNodes.Select(n => n.Id));
    }

    [Fact]
    public async Task Only_one_request_is_in_flight_and_the_newest_queued_box_is_sent_next()
    {
        var source = new FakeTileSource();
        var pane = Pane(source);
        pane.ApplyLodSnapshot(Lod(7));

        var first = Viewport(pane, new ConstellationBox(0, 0, 1, 1));
        await Viewport(pane, new ConstellationBox(2, 2, 3, 3));
        await Viewport(pane, new ConstellationBox(4, 4, 5, 5));
        Assert.Single(source.Requests);

        source.Responses[0].SetResult(Frame(7, 1, "a"));
        await UntilAsync(() => source.Requests.Count == 2);

        // The box at 2,2 was superseded before it was sent, so only 4,4 goes out.
        Assert.Equal(4, source.Requests[1].MinX);
        source.Responses[1].SetResult(Frame(7, 2, "c"));
        await first;

        Assert.Equal(["c"], pane.TileNodes.Select(n => n.Id));
        Assert.Equal(2, source.Requests.Count);
    }

    [Fact]
    public void Visible_box_is_the_camera_view_in_graph_coordinates()
    {
        var pane = Pane(new FakeTileSource());

        var box = pane.VisibleBox(800, 600);

        // At the default zoom and centre, the box is symmetric about the origin.
        Assert.True(box.MinX < 0 && box.MaxX > 0);
        Assert.True(box.MinY < 0 && box.MaxY > 0);
        Assert.Equal(-box.MinX, box.MaxX, precision: 3);
        Assert.Equal(-box.MinY, box.MaxY, precision: 3);
    }
}
