using Bazaar.Client.Core.Constellation;

namespace Bazaar.Client.Core.Tests.Constellation;

public class ConstellationNavigationTests
{
    [Fact]
    public void Focus_emits_a_request_with_the_view_id()
    {
        var nav = new ConstellationNavigation("view-1");

        var requests = nav.Focus("node-a");

        Assert.Equal([new ConstellationFocusRequest("view-1", "node-a")], requests);
        Assert.Equal("node-a", nav.FocusedNodeId);
        Assert.Equal(0, nav.BackDepth);
    }

    [Fact]
    public void Focusing_the_focused_node_is_a_no_op()
    {
        var nav = new ConstellationNavigation("view-1");
        nav.Focus("a");
        nav.Focus("b");

        var requests = nav.Focus("b");

        Assert.Empty(requests);
        Assert.Equal(1, nav.BackDepth);
    }

    [Fact]
    public void Back_and_forward_walk_the_history_like_a_browser()
    {
        var nav = new ConstellationNavigation("view-1");
        nav.Focus("a");
        nav.Focus("b");
        nav.Focus("c");

        Assert.Equal("b", Target(nav.Back()));
        Assert.Equal("a", Target(nav.Back()));
        Assert.Empty(nav.Back());
        Assert.Equal("a", nav.FocusedNodeId);

        Assert.Equal("b", Target(nav.Forward()));
        Assert.Equal("c", Target(nav.Forward()));
        Assert.Empty(nav.Forward());
        Assert.Equal("c", nav.FocusedNodeId);
    }

    [Fact]
    public void A_new_focus_after_back_clears_the_forward_stack()
    {
        var nav = new ConstellationNavigation("view-1");
        nav.Focus("a");
        nav.Focus("b");
        nav.Focus("c");
        nav.Back();
        nav.Back();
        Assert.Equal(2, nav.ForwardDepth);

        nav.Focus("x");

        Assert.Equal(0, nav.ForwardDepth);
        Assert.Empty(nav.Forward());
        Assert.Equal("x", nav.FocusedNodeId);
        Assert.Equal("a", Target(nav.Back()));
    }

    [Fact]
    public void Back_with_no_history_does_nothing()
    {
        var nav = new ConstellationNavigation("view-1");
        nav.Focus("a");

        Assert.Empty(nav.Back());
        Assert.Equal("a", nav.FocusedNodeId);
    }

    [Fact]
    public void History_is_bounded_and_drops_the_oldest_entries()
    {
        var nav = new ConstellationNavigation("view-1", historyLimit: 2);
        nav.Focus("a");
        nav.Focus("b");
        nav.Focus("c");
        nav.Focus("d");

        Assert.Equal(2, nav.BackDepth);
        Assert.Equal("c", Target(nav.Back()));
        Assert.Equal("b", Target(nav.Back()));
        Assert.Empty(nav.Back());
    }

    [Fact]
    public void Neighbour_steps_walk_the_ring_and_wrap()
    {
        var nav = new ConstellationNavigation("view-1");
        nav.Focus("hub");
        var adjacency = Adjacency("hub", "c", "a", "b");

        Assert.Equal("a", Target(nav.Apply(new ConstellationCommand(ConstellationCommandKind.NextNeighbour), adjacency)));
        Assert.Equal("b", Target(nav.Apply(new ConstellationCommand(ConstellationCommandKind.NextNeighbour), adjacency)));
        Assert.Equal("c", Target(nav.Apply(new ConstellationCommand(ConstellationCommandKind.NextNeighbour), adjacency)));
        Assert.Equal("a", Target(nav.Apply(new ConstellationCommand(ConstellationCommandKind.NextNeighbour), adjacency)));
        Assert.Equal("c", Target(nav.Apply(new ConstellationCommand(ConstellationCommandKind.PreviousNeighbour), adjacency)));
    }

    [Fact]
    public void Previous_neighbour_starts_at_the_last_neighbour()
    {
        var nav = new ConstellationNavigation("view-1");
        nav.Focus("hub");
        var adjacency = Adjacency("hub", "a", "b", "c");

        Assert.Equal("c", Target(nav.Apply(new ConstellationCommand(ConstellationCommandKind.PreviousNeighbour), adjacency)));
    }

    [Fact]
    public void Neighbour_step_without_neighbours_or_focus_does_nothing()
    {
        var nav = new ConstellationNavigation("view-1");
        var adjacency = Adjacency("hub", "a");

        Assert.Empty(nav.Apply(new ConstellationCommand(ConstellationCommandKind.NextNeighbour), adjacency));

        nav.Focus("lonely");
        Assert.Empty(nav.Apply(new ConstellationCommand(ConstellationCommandKind.NextNeighbour), adjacency));
        Assert.Empty(nav.Apply(new ConstellationCommand(ConstellationCommandKind.NextNeighbour), null));
    }

    [Fact]
    public void Neighbour_walk_ends_on_a_plain_focus_and_is_recorded_in_history()
    {
        var nav = new ConstellationNavigation("view-1");
        nav.Focus("hub");
        var adjacency = Adjacency("hub", "a", "b");
        nav.Apply(new ConstellationCommand(ConstellationCommandKind.NextNeighbour), adjacency);
        Assert.Equal(1, nav.BackDepth);

        nav.Focus("z");
        Assert.Equal("a", Target(nav.Back()));

        // The walk has ended, so the next step starts from the new focus again.
        var fresh = Adjacency("a", "q");
        Assert.Equal("q", Target(nav.Apply(new ConstellationCommand(ConstellationCommandKind.NextNeighbour), fresh)));
    }

    [Fact]
    public void Zoom_steps_through_tiers_and_clamps_at_the_ends()
    {
        var nav = new ConstellationNavigation("view-1");
        Assert.Equal(ZoomTier.Far, nav.Zoom);

        Assert.Empty(nav.Apply(new ConstellationCommand(ConstellationCommandKind.ZoomIn)));
        Assert.Equal(ZoomTier.Mid, nav.Zoom);
        nav.Apply(new ConstellationCommand(ConstellationCommandKind.ZoomIn));
        nav.Apply(new ConstellationCommand(ConstellationCommandKind.ZoomIn));
        Assert.Equal(ZoomTier.Near, nav.Zoom);

        nav.Apply(new ConstellationCommand(ConstellationCommandKind.ZoomOut));
        Assert.Equal(ZoomTier.Mid, nav.Zoom);
        nav.Apply(new ConstellationCommand(ConstellationCommandKind.ZoomOut));
        nav.Apply(new ConstellationCommand(ConstellationCommandKind.ZoomOut));
        Assert.Equal(ZoomTier.Far, nav.Zoom);
    }

    [Fact]
    public void Zoom_changes_do_not_touch_focus_history()
    {
        var nav = new ConstellationNavigation("view-1");
        nav.Focus("a");

        nav.Apply(new ConstellationCommand(ConstellationCommandKind.ZoomIn));

        Assert.Equal("a", nav.FocusedNodeId);
        Assert.Equal(0, nav.BackDepth);
    }

    [Fact]
    public void Focus_command_without_a_node_is_ignored()
    {
        var nav = new ConstellationNavigation("view-1");

        Assert.Empty(nav.Apply(new ConstellationCommand(ConstellationCommandKind.Focus)));
        Assert.Null(nav.FocusedNodeId);
    }

    [Fact]
    public void Apply_dispatches_focus_and_history_commands()
    {
        var nav = new ConstellationNavigation("view-1");

        nav.Apply(new ConstellationCommand(ConstellationCommandKind.Focus, "a"));
        nav.Apply(new ConstellationCommand(ConstellationCommandKind.Focus, "b"));
        Assert.Equal("a", Target(nav.Apply(new ConstellationCommand(ConstellationCommandKind.Back))));
        Assert.Equal("b", Target(nav.Apply(new ConstellationCommand(ConstellationCommandKind.Forward))));
    }

    [Fact]
    public void Empty_view_id_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new ConstellationNavigation(" "));
    }

    private static string Target(IReadOnlyList<ConstellationFocusRequest> requests)
    {
        Assert.Single(requests);
        return requests[0].NodeId;
    }

    private static Dictionary<string, IReadOnlyList<string>> Adjacency(string node, params string[] neighbours)
    {
        return new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { [node] = neighbours };
    }
}
