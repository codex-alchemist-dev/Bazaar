using Bazaar.Contracts.Graph;

namespace Bazaar.Contracts.Tests.Graph;

/// <summary>Tile and viewport contracts (doc 19): the invariants the records enforce on construction.</summary>
public sealed class GraphTileRecordsTests
{
    [Fact]
    public void ZoomTiers_are_ordered_from_coarsest_to_finest()
    {
        Assert.True(ZoomTier.Far < ZoomTier.Mid);
        Assert.True(ZoomTier.Mid < ZoomTier.Near);
    }

    [Fact]
    public void TileKey_defaults_z_to_zero_for_a_2d_view()
    {
        var key = new TileKey(2, 3, 4);

        Assert.Equal(0, key.Z);
        Assert.Equal(new TileKey(2, 3, 4, 0), key);
    }

    [Fact]
    public void TileKey_rejects_a_negative_level()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TileKey(-1, 0, 0));
    }

    [Fact]
    public void ViewportRequest_rejects_an_inverted_box()
    {
        Assert.Throws<ArgumentException>(() =>
            new ViewportRequest("view-a", 1, ZoomTier.Mid, 5, 0, 0, 1, 10, 0, 100));
    }

    [Fact]
    public void ViewportRequest_rejects_a_non_positive_node_cap()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ViewportRequest("view-a", 1, ZoomTier.Mid, 0, 0, 0, 1, 1, 0, 0));
    }

    [Fact]
    public void ViewportRequest_accepts_a_flat_2d_box()
    {
        var request = new ViewportRequest("view-a", 1, ZoomTier.Far, -10, -10, 0, 10, 10, 0, 500);

        Assert.Equal(0, request.MinZ);
        Assert.Equal(500, request.MaxNodes);
    }

    [Fact]
    public void ClusterGlyph_rejects_a_negative_member_count()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ClusterGlyph("c1", -1, 0, 0, 0));
    }

    [Fact]
    public void TileFrame_accepts_three_floats_per_node()
    {
        var frame = new TileFrame(
            "view-a", 1, 7, new TileKey(1, 0, 0), ZoomTier.Mid, 2,
            ["n1", "n2"], new float[] { 0, 0, 0, 1, 2, 0 }, []);

        Assert.Equal(2, frame.NodeIds.Count);
        Assert.Equal(6, frame.Xyz.Length);
    }

    [Theory]
    [InlineData((byte)1)]
    [InlineData((byte)4)]
    public void TileFrame_rejects_a_dimension_other_than_2_or_3(byte dimensions)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TileFrame("view-a", 1, 1, new TileKey(0, 0, 0), ZoomTier.Far, dimensions, [], ReadOnlyMemory<float>.Empty, []));
    }

    [Fact]
    public void TileFrame_rejects_a_position_buffer_of_the_wrong_length()
    {
        Assert.Throws<ArgumentException>(() =>
            new TileFrame("view-a", 1, 1, new TileKey(0, 0, 0), ZoomTier.Mid, 2, ["n1"], new float[] { 0, 0 }, []));
    }
}
