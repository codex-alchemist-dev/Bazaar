using Bazaar.Constellation.Rendering.Core;

namespace Bazaar.Constellation.Rendering.Tests;

public class NodeStylePaletteTests
{
    private static readonly NodeStyle _red = NodeStyle.Create(1f, 0f, 0f, 1f, 2f);
    private static readonly NodeStyle _blue = NodeStyle.Create(0f, 0f, 1f, 1f, 3f, ringWidth: 0.5f);

    [Fact]
    public void A_uniform_table_becomes_one_style()
    {
        NodeStylePalette palette = NodeStylePalette.Build(new NodeStyleTable(4, _red));

        Assert.Equal(4, palette.NodeCount);
        Assert.Equal(1, palette.StyleCount);
        Assert.All(Enumerable.Range(0, 4), node => Assert.Equal(0, palette.IndexOf(node)));
        Assert.Equal(_red, palette.GetStyle(0));
    }

    [Fact]
    public void Distinct_styles_get_indices_in_order_of_first_use()
    {
        var table = new NodeStyleTable(4, _red);
        table.Set(1, _blue);
        table.Set(3, _blue);

        NodeStylePalette palette = NodeStylePalette.Build(table);

        Assert.Equal(2, palette.StyleCount);
        Assert.Equal(new[] { 0, 1, 0, 1 }, Enumerable.Range(0, 4).Select(palette.IndexOf).ToArray());
        Assert.Equal(_red, palette.GetStyle(0));
        Assert.Equal(_blue, palette.GetStyle(1));
    }

    [Fact]
    public void An_empty_table_has_no_styles()
    {
        NodeStylePalette palette = NodeStylePalette.Build(new NodeStyleTable(0, _red));

        Assert.Equal(0, palette.NodeCount);
        Assert.Equal(0, palette.StyleCount);
    }

    [Fact]
    public void A_snapshot_does_not_follow_later_changes_to_the_table()
    {
        var table = new NodeStyleTable(2, _red);
        NodeStylePalette palette = NodeStylePalette.Build(table);

        table.Set(0, _blue);

        Assert.Equal(0, palette.IndexOf(0));
        Assert.Equal(_red, palette.GetStyle(palette.IndexOf(0)));
    }

    [Fact]
    public void Out_of_range_node_and_style_indices_are_rejected()
    {
        NodeStylePalette palette = NodeStylePalette.Build(new NodeStyleTable(2, _red));

        Assert.Throws<ArgumentOutOfRangeException>(() => palette.IndexOf(2));
        Assert.Throws<ArgumentOutOfRangeException>(() => palette.IndexOf(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => palette.GetStyle(1));
    }

    [Fact]
    public void Build_rejects_a_null_table()
    {
        Assert.Throws<ArgumentNullException>(() => NodeStylePalette.Build(null!));
    }
}
