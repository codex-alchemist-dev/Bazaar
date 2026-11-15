using Bazaar.Constellation.Rendering.Core;

namespace Bazaar.Constellation.Rendering.Tests;

public class ConstellationSceneTests
{
    [Fact]
    public void Accepts_valid_edges_and_clusters()
    {
        var scene = new ConstellationScene(3, new[] { 0, 1, 1, 2 }, new[] { 0, 0, 1 });
        Assert.Equal(2, scene.EdgeCount);
    }

    [Fact]
    public void Rejects_odd_edge_lists_and_out_of_range_endpoints()
    {
        Assert.Throws<ArgumentException>(() => new ConstellationScene(3, new[] { 0, 1, 2 }, default));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConstellationScene(3, new[] { 0, 3 }, default));
    }

    [Fact]
    public void Rejects_cluster_lists_that_do_not_match_node_count()
    {
        Assert.Throws<ArgumentException>(() => new ConstellationScene(3, default, new[] { 0, 1 }));
    }
}
