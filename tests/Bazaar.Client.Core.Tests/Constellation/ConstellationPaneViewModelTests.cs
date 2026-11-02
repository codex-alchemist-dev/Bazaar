using Bazaar.Client.Core.Constellation;
using Bazaar.Client.Core.Panes;
using Bazaar.Constellation.Rendering;
using Bazaar.Client.Core.Tests.Panes;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Core.Tests.Constellation;

public class ConstellationPaneViewModelTests
{
    private const string Model = """
        {"nodes":[
          {"id":"a","label":"Alpha","kind":"note"},
          {"id":"b","label":"Beta","kind":"note"},
          {"id":"c","label":"Gamma","kind":"memory"}],
         "edges":[{"from":"a","to":"b"},{"from":"b","to":"zz"},{"from":"c","to":"a"}]}
        """;

    private static ConstellationPaneViewModel Pane(string model = Model) =>
        new(PaneRegistryTests.Descriptor(PaneKinds.Constellation, model));

    [Fact]
    public void Parses_nodes_and_edges_and_skips_edges_with_unknown_endpoints()
    {
        var pane = Pane();

        Assert.Equal(["a", "b", "c"], pane.Nodes.Select(n => n.Id));
        Assert.Equal(2, pane.Scene.EdgeCount);
        Assert.Equal(1, pane.SkippedEdgeCount);
        Assert.Equal(1, pane.IndexOf("b"));
        Assert.Equal(-1, pane.IndexOf("missing"));
    }

    [Fact]
    public void Duplicate_and_empty_nodes_are_left_out()
    {
        var pane = Pane("""{"nodes":[{"id":"a"},{"id":"a","label":"Again"},{"id":""},{"label":"no id"}]}""");

        Assert.Single(pane.Nodes);
        Assert.Equal("a", pane.Nodes[0].Id);
        Assert.Equal("a", pane.Nodes[0].Label);
    }

    [Fact]
    public void Accepts_a_newer_2d_frame_and_rejects_wrong_size_3d_or_stale_frames()
    {
        var pane = Pane();

        Assert.False(pane.ApplyFrame(new RenderFrame(1, 1, 2, new float[] { 0, 0, 1, 1 }), 0), "wrong node count");
        Assert.False(pane.ApplyFrame(new RenderFrame(1, 1, 3, new float[] { 0, 0, 0, 1, 1, 1, 2, 2, 2 }), 0), "3D frame");
        Assert.True(pane.ApplyFrame(new RenderFrame(1, 5, 2, new float[] { 0, 0, 10, 0, 20, 0 }), 0));
        Assert.False(pane.ApplyFrame(new RenderFrame(1, 4, 2, new float[] { 0, 0, 10, 0, 20, 0 }), 1), "older layout");
        Assert.True(pane.ApplyFrame(new RenderFrame(2, 1, 2, new float[] { 0, 0, 10, 0, 20, 0 }), 1), "newer graph");
    }

    [Fact]
    public void Focus_moves_through_navigation_and_ignores_unknown_nodes()
    {
        var pane = Pane();
        var changed = new List<string>();
        pane.PropertyChanged += (_, e) => changed.Add(e.PropertyName!);

        Assert.Empty(pane.Command(new ConstellationCommand(ConstellationCommandKind.Focus, "missing")));
        Assert.Null(pane.FocusedNodeId);

        var requests = pane.Command(new ConstellationCommand(ConstellationCommandKind.Focus, "b"));
        Assert.Single(requests);
        Assert.Equal("b", pane.FocusedNodeId);
        Assert.True(pane.IsFocused("b"));
        Assert.Equal(["FocusedNodeId"], changed);
        Assert.Contains(pane.AccessibleList.Items, item => item.NodeId == "b" && item.IsFocused);
    }

    [Fact]
    public void Hit_test_grid_finds_the_nearest_node_after_a_frame()
    {
        var pane = Pane();
        pane.ApplyFrame(new RenderFrame(1, 1, 2, new float[] { 0, 0, 100, 0, 0, 100 }), 0);

        Assert.Equal(0, pane.Grid.NearestNode(1, 1, 10));
        Assert.Equal(1, pane.Grid.NearestNode(99, 0, 10));
        Assert.Equal(-1, pane.Grid.NearestNode(50, 50, 5));
    }

    [Fact]
    public void Unknown_kind_still_routes_through_the_registry_to_this_view_model()
    {
        var registry = FirstPartyPanes.CreateRegistry();

        Assert.IsType<ConstellationPaneViewModel>(registry.Create(PaneRegistryTests.Descriptor(PaneKinds.Constellation, Model)));
    }
}
