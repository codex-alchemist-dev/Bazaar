using Bazaar.Constellation.Rendering.Core;

namespace Bazaar.Constellation.Rendering.Tests;

public class NodeStyleTableTests
{
    private static readonly NodeStyle _fallback = NodeStyle.Create(0.5f, 0.5f, 0.5f, 1f, 2f);

    [Fact]
    public void New_table_holds_the_fallback_for_every_node()
    {
        var table = new NodeStyleTable(3, _fallback);
        Assert.Equal(3, table.NodeCount);
        for (int node = 0; node < 3; node++)
        {
            Assert.Equal(_fallback, table.Get(node));
        }
    }

    [Fact]
    public void Set_replaces_one_node_only()
    {
        var table = new NodeStyleTable(2, _fallback);
        NodeStyle bright = NodeStyle.Create(1f, 0f, 0f, 1f, 9f);
        table.Set(1, bright);
        Assert.Equal(bright, table.Get(1));
        Assert.Equal(_fallback, table.Get(0));
    }

    [Fact]
    public void Zero_node_table_is_valid()
    {
        var table = new NodeStyleTable(0, _fallback);
        Assert.Equal(0, table.NodeCount);
    }

    [Fact]
    public void Negative_node_count_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new NodeStyleTable(-1, _fallback));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void Get_and_Set_reject_indices_outside_the_table(int node)
    {
        var table = new NodeStyleTable(2, _fallback);
        Assert.Throws<ArgumentOutOfRangeException>(() => table.Get(node));
        Assert.Throws<ArgumentOutOfRangeException>(() => table.Set(node, _fallback));
    }
}
