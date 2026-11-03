using Bazaar.Server.Constellation.Lod;

namespace Bazaar.Server.Tests.Constellation.Lod;

public class LodFeedTests
{
    // Two clusters of three nodes on a line, each cluster well separated in x.
    private static readonly float[] _positions2D =
    [
        0f, 0f,  1f, 0f,  2f, 0f,
        10f, 0f, 11f, 0f, 12f, 0f,
    ];

    private static readonly int[] _clusters = [0, 0, 0, 1, 1, 1];

    private static readonly (int From, int To, int Weight)[] _edges =
    [
        (0, 1, 1), (1, 2, 1), // inside cluster 0
        (3, 4, 1),            // inside cluster 1
        (2, 3, 1),            // crosses to cluster 1
        (0, 5, 2),            // crosses again, weight 2
    ];

    [Fact]
    public void Cluster_counts_sum_to_node_count_and_centroids_are_member_means()
    {
        var feed = LodFeed.Build(_positions2D, 2, _clusters, _edges, nodeCap: 100, tileGridSize: 4);

        Assert.Equal(6, feed.Clusters.Sum(c => c.Count));
        Assert.Equal(3, feed.Clusters.Single(c => c.Cluster == 0).Count);
        Assert.Equal([1f, 0f], feed.Clusters.Single(c => c.Cluster == 0).Centroid);
        Assert.Equal([11f, 0f], feed.Clusters.Single(c => c.Cluster == 1).Centroid);
    }

    [Fact]
    public void Cluster_edges_keep_only_crossings_and_sum_their_weights()
    {
        var feed = LodFeed.Build(_positions2D, 2, _clusters, _edges, nodeCap: 100, tileGridSize: 4);

        var edge = Assert.Single(feed.ClusterEdges);
        Assert.Equal((0, 1), (edge.A, edge.B));
        Assert.Equal(3, edge.Weight);
    }

    [Fact]
    public void Tiles_partition_every_node_exactly_once_when_under_the_cap()
    {
        var feed = LodFeed.Build(_positions2D, 2, _clusters, _edges, nodeCap: 100, tileGridSize: 4);

        Assert.False(feed.ClustersOnly);
        var seen = feed.Tiles.SelectMany(t => t.NodeIndices).OrderBy(i => i).ToArray();
        Assert.Equal([0, 1, 2, 3, 4, 5], seen);
    }

    [Fact]
    public void Above_the_cap_the_feed_is_clusters_only_and_has_no_tiles()
    {
        var feed = LodFeed.Build(_positions2D, 2, _clusters, _edges, nodeCap: 5, tileGridSize: 4);

        Assert.True(feed.ClustersOnly);
        Assert.Empty(feed.Tiles);
        Assert.Equal(6, feed.Clusters.Sum(c => c.Count));
    }

    [Fact]
    public void A_graph_exactly_at_the_cap_still_gets_tiles()
    {
        var feed = LodFeed.Build(_positions2D, 2, _clusters, _edges, nodeCap: 6, tileGridSize: 4);

        Assert.False(feed.ClustersOnly);
    }

    [Fact]
    public void Three_dimensional_positions_centroid_all_axes_and_tile_in_the_plane()
    {
        float[] positions =
        [
            0f, 0f, 5f,
            10f, 0f, 7f,
        ];

        var feed = LodFeed.Build(positions, 3, [0, 0], [], nodeCap: 100, tileGridSize: 2);

        Assert.Equal([5f, 0f, 6f], feed.Clusters.Single().Centroid);
        Assert.Equal(2, feed.Tiles.Sum(t => t.NodeIndices.Length));
    }

    [Fact]
    public void Output_is_deterministic_for_the_same_input()
    {
        var first = LodFeed.Build(_positions2D, 2, _clusters, _edges, nodeCap: 100, tileGridSize: 4);
        var second = LodFeed.Build(_positions2D, 2, _clusters, _edges, nodeCap: 100, tileGridSize: 4);

        Assert.Equal(first.Clusters.Select(c => (c.Cluster, c.Count)), second.Clusters.Select(c => (c.Cluster, c.Count)));
        Assert.Equal(first.Tiles.Select(t => (t.Col, t.Row, string.Join(",", t.NodeIndices))), second.Tiles.Select(t => (t.Col, t.Row, string.Join(",", t.NodeIndices))));
    }

    [Fact]
    public void Coincident_points_do_not_divide_by_zero()
    {
        float[] positions = [3f, 3f, 3f, 3f];

        var feed = LodFeed.Build(positions, 2, [0, 0], [], nodeCap: 100, tileGridSize: 8);

        Assert.Equal(2, feed.Tiles.Sum(t => t.NodeIndices.Length));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void Bad_dimensions_are_rejected(int dimensions)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LodFeed.Build(_positions2D, dimensions, _clusters, _edges, 100, 4));
    }

    [Fact]
    public void Non_finite_positions_are_rejected()
    {
        float[] positions = [0f, float.NaN, 1f, 1f];

        Assert.Throws<ArgumentException>(() => LodFeed.Build(positions, 2, [0, 0], [], 100, 4));
    }

    [Fact]
    public void Cluster_list_must_match_the_node_count()
    {
        Assert.Throws<ArgumentException>(() => LodFeed.Build(_positions2D, 2, [0, 0], _edges, 100, 4));
    }

    [Fact]
    public void Edge_endpoints_must_be_in_range()
    {
        var bad = new[] { (0, 9, 1) };

        Assert.Throws<ArgumentOutOfRangeException>(() => LodFeed.Build(_positions2D, 2, _clusters, bad, 100, 4));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(257)]
    public void Tile_grid_size_is_bounded(int tileGridSize)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LodFeed.Build(_positions2D, 2, _clusters, _edges, 100, tileGridSize));
    }

    [Fact]
    public void Node_cap_must_be_positive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LodFeed.Build(_positions2D, 2, _clusters, _edges, 0, 4));
    }
}
