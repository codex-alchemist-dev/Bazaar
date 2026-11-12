using Bazaar.Constellation.Rendering;
using Bazaar.Constellation.Rendering.Core;

namespace Bazaar.Constellation.Rendering.Tests;

public class ConstellationNodeStylesTests
{
    private static readonly NodeStyle _fallback = NodeStyle.Create(0.5f, 0.5f, 0.5f, 1f, 2f);

    [Fact]
    public void No_clusters_gives_the_fallback_to_every_node()
    {
        var scene = new ConstellationScene(3, ReadOnlyMemory<int>.Empty, ReadOnlyMemory<int>.Empty);
        NodeStyleTable table = ConstellationNodeStyles.Build(scene, _fallback);

        Assert.Equal(3, table.NodeCount);
        for (int node = 0; node < 3; node++)
        {
            Assert.Equal(_fallback, table.Get(node));
        }
    }

    [Fact]
    public void Each_node_gets_the_style_of_its_cluster()
    {
        int[] clusters = [0, 3, 8, 1];
        var scene = new ConstellationScene(4, ReadOnlyMemory<int>.Empty, clusters);
        NodeStyleTable table = ConstellationNodeStyles.Build(scene, _fallback);

        for (int node = 0; node < clusters.Length; node++)
        {
            Assert.Equal(ConstellationNodeStyles.ForCluster(clusters[node]), table.Get(node));
        }
    }

    [Fact]
    public void Equal_cluster_ids_get_equal_styles()
    {
        int[] clusters = [5, 2, 5, 2];
        var scene = new ConstellationScene(4, ReadOnlyMemory<int>.Empty, clusters);
        NodeStyleTable table = ConstellationNodeStyles.Build(scene, _fallback);

        Assert.Equal(table.Get(0), table.Get(2));
        Assert.Equal(table.Get(1), table.Get(3));
    }

    [Fact]
    public void Cluster_ids_one_palette_length_apart_share_a_style()
    {
        int count = ConstellationNodeStyles.Palette.Count;
        Assert.Equal(ConstellationNodeStyles.ForCluster(1), ConstellationNodeStyles.ForCluster(1 + count));
    }

    [Fact]
    public void Negative_cluster_id_maps_into_the_palette()
    {
        int count = ConstellationNodeStyles.Palette.Count;
        NodeStyle style = ConstellationNodeStyles.ForCluster(-1);

        Assert.Contains(style, ConstellationNodeStyles.Palette);
        Assert.Equal(ConstellationNodeStyles.Palette[count - 1], style);
        Assert.Equal(ConstellationNodeStyles.Palette[0], ConstellationNodeStyles.ForCluster(-count));
    }

    [Fact]
    public void Every_palette_entry_passes_validation()
    {
        Assert.True(ConstellationNodeStyles.Palette.Count >= 8);
        foreach (NodeStyle style in ConstellationNodeStyles.Palette)
        {
            NodeStyle checkedStyle = NodeStyle.Create(style.Red, style.Green, style.Blue, style.Alpha, style.Radius);
            Assert.Equal(style, checkedStyle);
        }
    }

    [Fact]
    public void Zero_node_scene_gives_an_empty_table()
    {
        var scene = new ConstellationScene(0, ReadOnlyMemory<int>.Empty, ReadOnlyMemory<int>.Empty);
        NodeStyleTable table = ConstellationNodeStyles.Build(scene, _fallback);

        Assert.Equal(0, table.NodeCount);
    }

    [Fact]
    public void Null_scene_throws()
    {
        Assert.Throws<ArgumentNullException>(() => ConstellationNodeStyles.Build(null!, _fallback));
    }
}
