using System.Globalization;

namespace Bazaar.Client.Windowing.Tests;

/// <summary>Shape helper for dock trees. Records hold tab lists by reference, so tests compare this text instead.</summary>
internal static class DockShape
{
    public static DockPane Pane(string id) => new("chat", id);

    public static string Of(DockNode? node)
    {
        return node switch
        {
            null => "null",
            DockPane pane => pane.PaneId,
            DockTabs tabs => $"tabs[{string.Join(",", tabs.Tabs.Select(p => p.PaneId))}]@{tabs.ActiveIndex}",
            DockSplit split => Split(split),
            _ => throw new InvalidOperationException("Unknown node."),
        };
    }

    public static int Depth(DockNode? node)
    {
        return node switch
        {
            null => 0,
            DockSplit split => 1 + Math.Max(Depth(split.First), Depth(split.Second)),
            _ => 1,
        };
    }

    private static string Split(DockSplit split)
    {
        var orientation = split.Orientation == DockOrientation.Horizontal ? "h" : "v";
        var ratio = split.Ratio.ToString("R", CultureInfo.InvariantCulture);
        return $"{orientation}({ratio};{Of(split.First)};{Of(split.Second)})";
    }

    /// <summary>A tab group of the given pane ids with the given active index.</summary>
    public static DockTabs Tabs(int active, params ReadOnlySpan<string> ids)
    {
        var panes = new List<DockPane>();
        foreach (var id in ids)
        {
            panes.Add(Pane(id));
        }

        return new DockTabs(panes, active);
    }
}

/// <summary>Pure dock operations: detach, dock, tab activation and move, and their edge cases.</summary>
public sealed class DockOpsTests
{
    [Fact]
    public void Panes_lists_leaves_in_document_order()
    {
        var root = new DockSplit(
            DockOrientation.Horizontal,
            0.5,
            DockShape.Tabs(0, "a", "b"),
            new DockSplit(DockOrientation.Vertical, 0.5, DockShape.Pane("c"), DockShape.Pane("d")));

        var ids = DockOps.Panes(root).Select(p => p.PaneId);

        Assert.Equal("a,b,c,d", string.Join(",", ids));
    }

    [Fact]
    public void Panes_of_null_is_empty()
    {
        Assert.Empty(DockOps.Panes(null));
    }

    [Fact]
    public void Find_returns_pane_inside_a_tab_group()
    {
        var root = DockShape.Tabs(0, "a", "b");

        Assert.Equal(DockShape.Pane("b"), DockOps.Find(root, "b"));
    }

    [Fact]
    public void Find_unknown_id_returns_null()
    {
        Assert.Null(DockOps.Find(DockShape.Tabs(0, "a"), "zzz"));
        Assert.Null(DockOps.Find(null, "a"));
    }

    [Fact]
    public void Detach_from_tab_group_keeps_a_tab_group_and_shifts_active()
    {
        var result = DockOps.Detach(DockShape.Tabs(2, "a", "b", "c"), "b");

        Assert.Equal("tabs[a,c]@1", DockShape.Of(result.Remaining));
        Assert.Equal(DockShape.Pane("b"), result.Pane);
    }

    [Fact]
    public void Detach_last_but_one_tab_leaves_a_single_pane_tab_group()
    {
        var result = DockOps.Detach(DockShape.Tabs(0, "a", "b"), "a");

        Assert.IsType<DockTabs>(result.Remaining);
        Assert.Equal("tabs[b]@0", DockShape.Of(result.Remaining));
    }

    [Fact]
    public void Detach_active_tab_clamps_active_index()
    {
        var result = DockOps.Detach(DockShape.Tabs(1, "a", "b"), "b");

        Assert.Equal("tabs[a]@0", DockShape.Of(result.Remaining));
    }

    [Fact]
    public void Detach_from_split_collapses_to_the_surviving_sibling()
    {
        var root = new DockSplit(DockOrientation.Horizontal, 0.5, DockShape.Pane("a"), DockShape.Pane("b"));

        var result = DockOps.Detach(root, "a");

        Assert.Equal("b", DockShape.Of(result.Remaining));
        Assert.Equal(DockShape.Pane("a"), result.Pane);
    }

    [Fact]
    public void Detach_emptied_tab_group_collapses_its_parent_split()
    {
        var root = new DockSplit(DockOrientation.Horizontal, 0.4, DockShape.Pane("x"), DockShape.Pane("y"));
        var deeper = new DockSplit(DockOrientation.Vertical, 0.6, root, DockShape.Pane("z"));

        var result = DockOps.Detach(deeper, "x");

        Assert.Equal("v(0.6;y;z)", DockShape.Of(result.Remaining));
    }

    [Fact]
    public void Detach_nested_pane_keeps_split_ratio_and_orientation()
    {
        var root = new DockSplit(
            DockOrientation.Horizontal,
            0.3,
            DockShape.Pane("a"),
            new DockSplit(DockOrientation.Vertical, 0.7, DockShape.Pane("b"), DockShape.Pane("c")));

        var result = DockOps.Detach(root, "b");

        Assert.Equal("h(0.3;a;c)", DockShape.Of(result.Remaining));
    }

    [Fact]
    public void Detach_lone_pane_gives_null_remaining()
    {
        var result = DockOps.Detach(DockShape.Pane("a"), "a");

        Assert.Null(result.Remaining);
        Assert.Equal(DockShape.Pane("a"), result.Pane);
    }

    [Fact]
    public void Detach_unknown_id_returns_root_unchanged()
    {
        var root = DockShape.Tabs(0, "a", "b");

        var result = DockOps.Detach(root, "zzz");

        Assert.Same(root, result.Remaining);
        Assert.Null(result.Pane);
    }

    [Fact]
    public void Detach_from_null_root_is_empty()
    {
        var result = DockOps.Detach(null, "a");

        Assert.Null(result.Remaining);
        Assert.Null(result.Pane);
    }

    [Fact]
    public void Detach_does_not_mutate_the_input_layout()
    {
        var root = DockShape.Tabs(1, "a", "b", "c");
        var before = DockShape.Of(root);

        _ = DockOps.Detach(root, "b");

        Assert.Equal(before, DockShape.Of(root));
    }

    [Fact]
    public void Dock_into_null_root_gives_a_tab_group_with_the_pane()
    {
        var result = DockOps.Dock(null, DockShape.Pane("a"), "ignored", DockEdge.Left);

        Assert.Equal("tabs[a]@0", DockShape.Of(result));
    }

    [Fact]
    public void Dock_center_on_a_bare_pane_makes_two_tabs_and_activates_the_new_one()
    {
        var result = DockOps.Dock(DockShape.Pane("a"), DockShape.Pane("b"), "a", DockEdge.Center);

        Assert.Equal("tabs[a,b]@1", DockShape.Of(result));
    }

    [Fact]
    public void Dock_center_on_a_tab_group_appends_and_activates_the_new_tab()
    {
        var result = DockOps.Dock(DockShape.Tabs(0, "a", "b"), DockShape.Pane("c"), "a", DockEdge.Center);

        Assert.Equal("tabs[a,b,c]@2", DockShape.Of(result));
    }

    [Theory]
    [InlineData(DockEdge.Left, "h(0.5;b;a)")]
    [InlineData(DockEdge.Right, "h(0.5;a;b)")]
    [InlineData(DockEdge.Top, "v(0.5;b;a)")]
    [InlineData(DockEdge.Bottom, "v(0.5;a;b)")]
    public void Dock_on_each_edge_wraps_the_target_in_a_half_split(DockEdge edge, string expected)
    {
        var result = DockOps.Dock(DockShape.Pane("a"), DockShape.Pane("b"), "a", edge);

        Assert.Equal(expected, DockShape.Of(result));
    }

    [Fact]
    public void Dock_on_an_edge_wraps_the_whole_tab_group_not_just_the_target_tab()
    {
        var root = DockShape.Tabs(1, "a", "b");

        var result = DockOps.Dock(root, DockShape.Pane("c"), "b", DockEdge.Right);

        Assert.Equal("h(0.5;tabs[a,b]@1;c)", DockShape.Of(result));
    }

    [Fact]
    public void Dock_a_pane_already_in_the_layout_returns_root_unchanged()
    {
        var root = DockShape.Tabs(0, "a", "b");

        var result = DockOps.Dock(root, DockShape.Pane("b"), "a", DockEdge.Center);

        Assert.Same(root, result);
    }

    [Fact]
    public void Dock_onto_an_unknown_target_returns_root_unchanged()
    {
        var root = DockShape.Tabs(0, "a", "b");

        var result = DockOps.Dock(root, DockShape.Pane("c"), "zzz", DockEdge.Top);

        Assert.Same(root, result);
    }

    [Fact]
    public void Dock_stops_at_the_maximum_nesting_depth()
    {
        DockNode tree = DockShape.Pane("p0");
        for (var i = 1; i < DockOps.MaxDepth; i++)
        {
            tree = DockOps.Dock(tree, DockShape.Pane($"p{i}"), $"p{i - 1}", DockEdge.Right);
        }

        Assert.Equal(DockOps.MaxDepth, DockShape.Depth(tree));

        var result = DockOps.Dock(tree, DockShape.Pane("overflow"), $"p{DockOps.MaxDepth - 1}", DockEdge.Right);

        Assert.Same(tree, result);
    }

    [Fact]
    public void Activate_tab_sets_the_active_index_of_its_group()
    {
        var result = DockOps.ActivateTab(DockShape.Tabs(0, "a", "b", "c"), "c");

        Assert.Equal("tabs[a,b,c]@2", DockShape.Of(result));
    }

    [Fact]
    public void Activate_unknown_id_returns_root_unchanged()
    {
        var root = DockShape.Tabs(0, "a", "b");

        Assert.Same(root, DockOps.ActivateTab(root, "zzz"));
        Assert.Null(DockOps.ActivateTab(null, "a"));
    }

    [Fact]
    public void Move_tab_reorders_within_the_group_and_keeps_it_active()
    {
        var result = DockOps.MoveTab(DockShape.Tabs(0, "a", "b", "c"), "a", 2);

        Assert.Equal("tabs[b,c,a]@2", DockShape.Of(result));
    }

    [Fact]
    public void Move_tab_clamps_the_index_to_the_group()
    {
        var root = DockShape.Tabs(0, "a", "b", "c");

        Assert.Equal("tabs[b,c,a]@2", DockShape.Of(DockOps.MoveTab(root, "a", 99)));
        Assert.Equal("tabs[c,a,b]@0", DockShape.Of(DockOps.MoveTab(root, "c", -5)));
    }

    [Fact]
    public void Move_tab_on_a_bare_pane_or_unknown_id_is_unchanged()
    {
        var pane = DockShape.Pane("a");

        Assert.Same(pane, DockOps.MoveTab(pane, "a", 0));
        Assert.Null(DockOps.MoveTab(null, "a", 0));
    }

    [Fact]
    public void Ratio_out_of_range_is_clamped_when_a_split_is_rebuilt()
    {
        var root = new DockSplit(
            DockOrientation.Horizontal,
            0.95,
            DockShape.Pane("c"),
            new DockSplit(DockOrientation.Vertical, 0.5, DockShape.Pane("a"), DockShape.Pane("b")));

        var result = DockOps.Detach(root, "b");

        Assert.Equal("h(0.9;c;a)", DockShape.Of(result.Remaining));
    }
}
