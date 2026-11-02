using Bazaar.Client.Core.Constellation;

namespace Bazaar.Client.Core.Tests.Constellation;

public class AccessibleNodeListTests
{
    [Fact]
    public void Orders_by_kind_then_label_and_keeps_ties_in_input_order()
    {
        var list = AccessibleNodeList.Create(
        [
            new AccessibleNodeInput("n3", "Beta", "pr"),
            new AccessibleNodeInput("n1", "Alpha", "vault"),
            new AccessibleNodeInput("n4", "Alpha", "pr"),
            new AccessibleNodeInput("n2", "Alpha", "vault"),
        ], focusedNodeId: null);

        Assert.Equal(
            ["n4", "n3", "n1", "n2"],
            list.Items.Select(i => i.NodeId).ToArray());
        Assert.Equal([0, 1, 2, 3], list.Items.Select(i => i.Index).ToArray());
    }

    [Fact]
    public void Flags_the_focused_item_and_reports_its_index()
    {
        var list = AccessibleNodeList.Create(
        [
            new AccessibleNodeInput("a", "Alpha", "vault"),
            new AccessibleNodeInput("b", "Beta", "vault"),
        ], focusedNodeId: "b");

        Assert.Equal(1, list.FocusedIndex);
        Assert.False(list.Items[0].IsFocused);
        Assert.True(list.Items[1].IsFocused);
    }

    [Fact]
    public void Focus_outside_the_list_gives_minus_one()
    {
        var list = AccessibleNodeList.Create(
        [
            new AccessibleNodeInput("a", "Alpha", "vault"),
        ], focusedNodeId: "missing");

        Assert.Equal(-1, list.FocusedIndex);
        Assert.All(list.Items, item => Assert.False(item.IsFocused));
    }

    [Fact]
    public void Next_wraps_from_last_to_first()
    {
        var list = Sample();

        Assert.Equal(1, list.NextIndex(0));
        Assert.Equal(2, list.NextIndex(1));
        Assert.Equal(0, list.NextIndex(2));
        Assert.Equal(0, list.NextIndex(-1));
    }

    [Fact]
    public void Previous_wraps_from_first_to_last()
    {
        var list = Sample();

        Assert.Equal(2, list.PreviousIndex(0));
        Assert.Equal(1, list.PreviousIndex(2));
        Assert.Equal(0, list.PreviousIndex(1));
        Assert.Equal(2, list.PreviousIndex(-1));
    }

    [Fact]
    public void Empty_list_has_no_navigable_index()
    {
        var list = AccessibleNodeList.Create([], focusedNodeId: null);

        Assert.Equal(0, list.Count);
        Assert.Equal(-1, list.FocusedIndex);
        Assert.Equal(-1, list.NextIndex(0));
        Assert.Equal(-1, list.PreviousIndex(0));
    }

    [Fact]
    public void Order_is_stable_across_rebuilds_with_the_same_input()
    {
        var input = new[]
        {
            new AccessibleNodeInput("x", "Same", "gc"),
            new AccessibleNodeInput("y", "Same", "gc"),
            new AccessibleNodeInput("z", "Same", "gc"),
        };

        var first = AccessibleNodeList.Create(input, focusedNodeId: "y");
        var second = AccessibleNodeList.Create(input, focusedNodeId: "z");

        Assert.Equal(
            first.Items.Select(i => i.NodeId).ToArray(),
            second.Items.Select(i => i.NodeId).ToArray());
        Assert.Equal(["x", "y", "z"], first.Items.Select(i => i.NodeId).ToArray());
    }

    [Fact]
    public void Duplicate_node_ids_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => AccessibleNodeList.Create(
        [
            new AccessibleNodeInput("a", "One", "vault"),
            new AccessibleNodeInput("a", "Two", "vault"),
        ], focusedNodeId: null));
    }

    private static AccessibleNodeList Sample() => AccessibleNodeList.Create(
    [
        new AccessibleNodeInput("a", "Alpha", "vault"),
        new AccessibleNodeInput("b", "Beta", "vault"),
        new AccessibleNodeInput("c", "Gamma", "vault"),
    ], focusedNodeId: null);
}
