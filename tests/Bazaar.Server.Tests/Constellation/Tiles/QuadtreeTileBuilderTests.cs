using Bazaar.Contracts.Graph;
using Bazaar.Server.Constellation.Tiles;

namespace Bazaar.Server.Tests.Constellation.Tiles;

public sealed class QuadtreeTileBuilderTests
{
    private static readonly string[] _oneId = ["a"];
    private static readonly string[] _twoIds = ["a", "b"];
    private static readonly float[] _zero = [0, 0, 0];
    private static readonly float[] _nanPosition = [float.NaN, 0, 0];

    /// <summary>Random positions with a fixed seed, so every test run sees the same layout.</summary>
    private static (IReadOnlyList<string> Ids, float[] Xyz) RandomLayout(int count, int seed = 7)
    {
        var random = new Random(seed);
        var ids = Enumerable.Range(0, count).Select(i => $"n{i}").ToArray();
        var xyz = new float[count * 3];
        for (var i = 0; i < count; i++)
        {
            xyz[3 * i] = (float)((random.NextDouble() * 2000) - 1000);
            xyz[(3 * i) + 1] = (float)((random.NextDouble() * 500) - 250);
            xyz[(3 * i) + 2] = (float)random.NextDouble();
        }

        return (ids, xyz);
    }

    private static IReadOnlyList<TileFrame> Build(IReadOnlyList<string> ids, float[] xyz, int maxDepth) =>
        QuadtreeTileBuilder.Build("view-1", 3, 1, ZoomTier.Far, ids, xyz, new QuadtreeTileOptions { MaxDepth = maxDepth });

    [Fact]
    public void Every_node_lands_in_exactly_one_tile_at_each_level()
    {
        var (ids, xyz) = RandomLayout(500);

        var tiles = Build(ids, xyz, maxDepth: 5);

        for (var level = 0; level <= 5; level++)
        {
            var atLevel = tiles.Where(t => t.Key.Level == level).ToArray();
            var placed = atLevel.SelectMany(t => t.NodeIds).ToArray();
            Assert.Equal(ids.Count, placed.Length);
            Assert.Equal(ids.Count, placed.Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(ids.OrderBy(id => id, StringComparer.Ordinal), placed.OrderBy(id => id, StringComparer.Ordinal));
        }
    }

    [Fact]
    public void Each_child_tile_lies_inside_its_parent_tile()
    {
        var (ids, xyz) = RandomLayout(300);

        var tiles = Build(ids, xyz, maxDepth: 4);
        var byKey = tiles.ToDictionary(t => t.Key);

        foreach (var child in tiles.Where(t => t.Key.Level > 0))
        {
            var parentKey = new TileKey(child.Key.Level - 1, child.Key.X >> 1, child.Key.Y >> 1, 0);
            Assert.True(byKey.TryGetValue(parentKey, out var parent));
            Assert.All(child.NodeIds, id => Assert.Contains(id, parent!.NodeIds));
        }
    }

    [Fact]
    public void Same_input_gives_the_same_tiles_in_the_same_order()
    {
        var (ids, xyz) = RandomLayout(400, seed: 99);

        var first = Build(ids, xyz, maxDepth: 4);
        var second = Build(ids, xyz, maxDepth: 4);

        Assert.Equal(first.Count, second.Count);
        for (var i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i].Key, second[i].Key);
            Assert.Equal(first[i].NodeIds, second[i].NodeIds);
            Assert.Equal(first[i].Xyz.ToArray(), second[i].Xyz.ToArray());
        }
    }

    [Fact]
    public void Empty_graph_yields_no_tiles()
    {
        var tiles = Build(Array.Empty<string>(), Array.Empty<float>(), maxDepth: 4);

        Assert.Empty(tiles);
    }

    [Fact]
    public void Max_depth_is_respected()
    {
        var (ids, xyz) = RandomLayout(200);

        var tiles = Build(ids, xyz, maxDepth: 3);

        Assert.Equal(0, tiles.Min(t => t.Key.Level));
        Assert.Equal(3, tiles.Max(t => t.Key.Level));
        Assert.All(tiles, t => Assert.InRange(t.Key.X, 0, (1 << t.Key.Level) - 1));
        Assert.All(tiles, t => Assert.InRange(t.Key.Y, 0, (1 << t.Key.Level) - 1));
    }

    [Fact]
    public void Max_depth_zero_gives_one_tile_holding_every_node()
    {
        var (ids, xyz) = RandomLayout(50);

        var tiles = Build(ids, xyz, maxDepth: 0);

        var tile = Assert.Single(tiles);
        Assert.Equal(new TileKey(0, 0, 0, 0), tile.Key);
        Assert.Equal(ids, tile.NodeIds);
    }

    [Fact]
    public void Corner_nodes_land_in_opposite_cells_at_level_one()
    {
        var ids = new[] { "origin", "far" };
        var xyz = new float[] { 0, 0, 0, 10, 10, 0 };

        var tiles = Build(ids, xyz, maxDepth: 1);

        var level1 = tiles.Where(t => t.Key.Level == 1).ToArray();
        Assert.Equal(2, level1.Length);
        Assert.Contains(level1, t => t.Key.X == 0 && t.Key.Y == 0 && t.NodeIds.Count == 1 && t.NodeIds[0] == "origin");
        Assert.Contains(level1, t => t.Key.X == 1 && t.Key.Y == 1 && t.NodeIds.Count == 1 && t.NodeIds[0] == "far");
    }

    [Fact]
    public void Coincident_nodes_share_one_tile_at_every_level()
    {
        var ids = new[] { "a", "b", "c" };
        var xyz = new float[] { 5, 5, 0, 5, 5, 0, 5, 5, 0 };

        var tiles = Build(ids, xyz, maxDepth: 3);

        Assert.Equal(4, tiles.Count);
        Assert.All(tiles, t => Assert.Equal(ids, t.NodeIds));
        Assert.All(tiles, t => Assert.Equal(0, t.Key.X + t.Key.Y));
    }

    [Fact]
    public void Tiles_are_two_dimensional_and_carry_the_request_fields()
    {
        var (ids, xyz) = RandomLayout(20);

        var tiles = QuadtreeTileBuilder.Build("view-9", 12, 4, ZoomTier.Mid, ids, xyz, new QuadtreeTileOptions { MaxDepth = 2 });

        Assert.All(tiles, t =>
        {
            Assert.Equal("view-9", t.ViewId);
            Assert.Equal(12L, t.GraphVersion);
            Assert.Equal(4L, t.LayoutSeq);
            Assert.Equal(ZoomTier.Mid, t.Tier);
            Assert.Equal(2, t.Dimensions);
            Assert.Empty(t.Clusters);
            Assert.Equal(t.NodeIds.Count * 3, t.Xyz.Length);
            Assert.All(Enumerable.Range(0, t.NodeIds.Count), k => Assert.Equal(0f, t.Xyz.Span[(3 * k) + 2]));
        });
    }

    [Fact]
    public void Tiles_round_trip_through_the_binary_codec()
    {
        var (ids, xyz) = RandomLayout(120);

        var tiles = Build(ids, xyz, maxDepth: 3);

        foreach (var tile in tiles)
        {
            var decoded = TileFrameBinaryCodec.Decode(TileFrameBinaryCodec.Encode(tile));

            Assert.Equal(tile.ViewId, decoded.ViewId);
            Assert.Equal(tile.GraphVersion, decoded.GraphVersion);
            Assert.Equal(tile.LayoutSeq, decoded.LayoutSeq);
            Assert.Equal(tile.Key, decoded.Key);
            Assert.Equal(tile.Tier, decoded.Tier);
            Assert.Equal(tile.Dimensions, decoded.Dimensions);
            Assert.Equal(tile.NodeIds, decoded.NodeIds);
            Assert.Equal(tile.Xyz.ToArray(), decoded.Xyz.ToArray());
            Assert.Empty(decoded.Clusters);
        }
    }

    [Fact]
    public void Negative_max_depth_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            QuadtreeTileBuilder.Build("v", 0, 0, ZoomTier.Far, _oneId, _zero, new QuadtreeTileOptions { MaxDepth = -1 }));
    }

    [Fact]
    public void Max_depth_above_the_supported_limit_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            QuadtreeTileBuilder.Build("v", 0, 0, ZoomTier.Far, _oneId, _zero,
                new QuadtreeTileOptions { MaxDepth = QuadtreeTileBuilder.MaxSupportedDepth + 1 }));
    }

    [Fact]
    public void Position_count_must_match_the_node_count()
    {
        Assert.Throws<ArgumentException>(() =>
            QuadtreeTileBuilder.Build("v", 0, 0, ZoomTier.Far, _twoIds, _zero));
    }

    [Fact]
    public void Non_finite_positions_are_rejected()
    {
        Assert.Throws<ArgumentException>(() =>
            QuadtreeTileBuilder.Build("v", 0, 0, ZoomTier.Far, _oneId, _nanPosition));
    }
}
