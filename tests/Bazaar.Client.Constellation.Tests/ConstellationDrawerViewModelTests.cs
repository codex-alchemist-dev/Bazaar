using System.ComponentModel;
using Bazaar.Client.Constellation.Abstractions;
using Bazaar.Contracts.Graph;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Constellation.Tests;

public class ConstellationDrawerViewModelTests
{
    [Fact]
    public async Task Without_a_graph_version_nothing_is_requested_and_the_status_says_so()
    {
        var tiles = DrawerTileSource.Answering(DrawerTestData.Frame(["a"]));
        var model = new ConstellationDrawerViewModel(tiles);

        await model.Refresh(DrawerTestData.Box, graph: null);

        Assert.Empty(tiles.Requests);
        Assert.Equal(DrawerStatus.NoGraph, model.Status);
        Assert.Empty(model.Nodes);
    }

    [Fact]
    public async Task A_graph_above_the_node_cap_lists_no_nodes_and_requests_none()
    {
        var tiles = DrawerTileSource.Answering(DrawerTestData.Frame(["a"]));
        var model = new ConstellationDrawerViewModel(tiles);

        await model.Refresh(DrawerTestData.Box, new DrawerGraphState(GraphVersion: 7, ClustersOnly: true));

        Assert.Empty(tiles.Requests);
        Assert.Equal(DrawerStatus.ClustersOnly, model.Status);
    }

    [Fact]
    public async Task A_frame_lists_its_nodes_in_server_order_with_their_positions()
    {
        var tiles = DrawerTileSource.Answering(DrawerTestData.Frame(["alpha", "beta", "gamma"]));
        var model = new ConstellationDrawerViewModel(tiles);

        await model.Refresh(DrawerTestData.Box, DrawerTestData.Graph);

        Assert.Equal(DrawerStatus.Ready, model.Status);
        Assert.Equal(
            [new DrawerNode("alpha", 0, 10), new DrawerNode("beta", 1, 11), new DrawerNode("gamma", 2, 12)],
            model.Nodes);
    }

    [Fact]
    public async Task The_request_asks_the_pane_view_for_the_box_at_the_graph_version_capped_at_the_node_cap()
    {
        var tiles = DrawerTileSource.Answering(DrawerTestData.Frame(["a"]));
        var model = new ConstellationDrawerViewModel(tiles, nodeCap: 50);
        var box = new ConstellationBox(-5, -6, 30, 40);

        await model.Refresh(box, new DrawerGraphState(GraphVersion: 9, ClustersOnly: false));

        var request = Assert.Single(tiles.Requests);
        Assert.Equal(PaneKinds.Constellation, request.ViewId);
        Assert.Equal(9, request.GraphVersion);
        Assert.Equal(Bazaar.Contracts.Graph.ZoomTier.Near, request.Tier);
        Assert.Equal((-5f, -6f, 30f, 40f), (request.MinX, request.MinY, request.MaxX, request.MaxY));
        Assert.Equal((0f, 0f), (request.MinZ, request.MaxZ));
        Assert.Equal(50, request.MaxNodes);
    }

    [Fact]
    public async Task An_empty_box_reports_empty_rather_than_ready()
    {
        var model = new ConstellationDrawerViewModel(DrawerTileSource.Answering(null));

        await model.Refresh(DrawerTestData.Box, DrawerTestData.Graph);

        Assert.Equal(DrawerStatus.Empty, model.Status);
        Assert.Empty(model.Nodes);
    }

    [Fact]
    public async Task A_frame_for_another_graph_version_is_dropped()
    {
        var tiles = DrawerTileSource.Answering(DrawerTestData.Frame(["stale"], graphVersion: 6));
        var model = new ConstellationDrawerViewModel(tiles);

        await model.Refresh(DrawerTestData.Box, DrawerTestData.Graph);

        Assert.Equal(DrawerStatus.NoGraph, model.Status);
        Assert.Empty(model.Nodes);
    }

    [Fact]
    public async Task A_frame_for_another_view_is_dropped()
    {
        var tiles = DrawerTileSource.Answering(DrawerTestData.Frame(["x"], viewId: "bazaar.other"));
        var model = new ConstellationDrawerViewModel(tiles);

        await model.Refresh(DrawerTestData.Box, DrawerTestData.Graph);

        Assert.Empty(model.Nodes);
        Assert.Equal(DrawerStatus.NoGraph, model.Status);
    }

    [Fact]
    public async Task A_frame_over_the_node_cap_is_dropped_and_the_previous_list_stays()
    {
        var frames = new Queue<TileFrame?>([DrawerTestData.Frame(["a"], layoutSeq: 1), DrawerTestData.Frame(["a", "b", "c"], layoutSeq: 2)]);
        var tiles = new DrawerTileSource(_ => Task.FromResult(frames.Dequeue()));
        var model = new ConstellationDrawerViewModel(tiles, nodeCap: 2);

        await model.Refresh(DrawerTestData.Box, DrawerTestData.Graph);
        await model.Refresh(DrawerTestData.Box, DrawerTestData.Graph);

        Assert.Equal(["a"], model.Nodes.Select(n => n.Id));
    }

    [Fact]
    public async Task An_older_layout_sequence_is_dropped()
    {
        var frames = new Queue<TileFrame?>([DrawerTestData.Frame(["new"], layoutSeq: 5), DrawerTestData.Frame(["old"], layoutSeq: 4)]);
        var tiles = new DrawerTileSource(_ => Task.FromResult(frames.Dequeue()));
        var model = new ConstellationDrawerViewModel(tiles);

        await model.Refresh(DrawerTestData.Box, DrawerTestData.Graph);
        await model.Refresh(DrawerTestData.Box, DrawerTestData.Graph);

        Assert.Equal(["new"], model.Nodes.Select(n => n.Id));
    }

    [Fact]
    public async Task A_new_graph_version_resets_the_layout_sequence()
    {
        var frames = new Queue<TileFrame?>([DrawerTestData.Frame(["old"], layoutSeq: 5), DrawerTestData.Frame(["next"], graphVersion: 8, layoutSeq: 1)]);
        var tiles = new DrawerTileSource(_ => Task.FromResult(frames.Dequeue()));
        var model = new ConstellationDrawerViewModel(tiles);

        await model.Refresh(DrawerTestData.Box, DrawerTestData.Graph);
        await model.Refresh(DrawerTestData.Box, new DrawerGraphState(GraphVersion: 8, ClustersOnly: false));

        Assert.Equal(["next"], model.Nodes.Select(n => n.Id));
    }

    [Fact]
    public async Task A_superseded_refresh_is_dropped_when_it_answers_late()
    {
        var late = new TaskCompletionSource<TileFrame?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tiles = new DrawerTileSource(request => request.MinX == 0
            ? late.Task
            : Task.FromResult<TileFrame?>(DrawerTestData.Frame(["current"], layoutSeq: 2)));
        var model = new ConstellationDrawerViewModel(tiles);
        var first = model.Refresh(new ConstellationBox(0, 0, 1, 1), DrawerTestData.Graph);

        await model.Refresh(new ConstellationBox(50, 50, 60, 60), DrawerTestData.Graph);
        late.SetResult(DrawerTestData.Frame(["stale"], layoutSeq: 3));
        await first;

        Assert.Equal(["current"], model.Nodes.Select(n => n.Id));
    }

    [Fact]
    public async Task Opening_a_listed_node_shows_its_id_and_position()
    {
        var model = new ConstellationDrawerViewModel(DrawerTileSource.Answering(DrawerTestData.Frame(["alpha", "beta"])));
        await model.Refresh(DrawerTestData.Box, DrawerTestData.Graph);

        Assert.True(model.OpenNode("beta"));

        Assert.Equal(new DrawerNode("beta", 1, 11), model.OpenedNode);
    }

    [Fact]
    public async Task Opening_an_unlisted_node_changes_nothing()
    {
        var model = new ConstellationDrawerViewModel(DrawerTileSource.Answering(DrawerTestData.Frame(["alpha"])));
        await model.Refresh(DrawerTestData.Box, DrawerTestData.Graph);

        Assert.False(model.OpenNode("missing"));

        Assert.Null(model.OpenedNode);
    }

    [Fact]
    public async Task Close_clears_the_opened_node()
    {
        var model = new ConstellationDrawerViewModel(DrawerTileSource.Answering(DrawerTestData.Frame(["alpha"])));
        await model.Refresh(DrawerTestData.Box, DrawerTestData.Graph);
        model.OpenNode("alpha");

        model.Close();

        Assert.Null(model.OpenedNode);
    }

    [Fact]
    public async Task A_refresh_that_no_longer_lists_the_opened_node_closes_it()
    {
        var frames = new Queue<TileFrame?>([DrawerTestData.Frame(["alpha", "beta"], layoutSeq: 1), DrawerTestData.Frame(["beta"], layoutSeq: 2)]);
        var model = new ConstellationDrawerViewModel(new DrawerTileSource(_ => Task.FromResult(frames.Dequeue())));
        await model.Refresh(DrawerTestData.Box, DrawerTestData.Graph);
        model.OpenNode("alpha");

        await model.Refresh(DrawerTestData.Box, DrawerTestData.Graph);

        Assert.Null(model.OpenedNode);
    }

    [Fact]
    public async Task Changes_raise_property_notifications_for_the_list_and_the_opened_node()
    {
        var model = new ConstellationDrawerViewModel(DrawerTileSource.Answering(DrawerTestData.Frame(["alpha"])));
        var raised = new List<string?>();
        model.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        await model.Refresh(DrawerTestData.Box, DrawerTestData.Graph);
        model.OpenNode("alpha");

        Assert.Contains(nameof(ConstellationDrawerViewModel.Status), raised);
        Assert.Contains(nameof(ConstellationDrawerViewModel.Nodes), raised);
        Assert.Contains(nameof(ConstellationDrawerViewModel.OpenedNode), raised);
    }

    [Fact]
    public void The_node_cap_defaults_to_the_phone_value_and_rejects_zero()
    {
        var model = new ConstellationDrawerViewModel(DrawerTileSource.Answering(null));

        Assert.Equal(200, ConstellationDrawerViewModel.DefaultNodeCap);
        Assert.Equal(ConstellationDrawerViewModel.DefaultNodeCap, model.NodeCap);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConstellationDrawerViewModel(DrawerTileSource.Answering(null), nodeCap: 0));
    }
}
