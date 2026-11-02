using System.Text.Json;
using Bazaar.Contracts.Graph;
using Bazaar.Contracts.Wire;

namespace Bazaar.Contracts.Tests.Wire;

/// <summary>Tile and LOD records through <see cref="WireJsonContext"/> (doc 19). Pure: no sockets, no database.</summary>
public class TileWireJsonTests
{
    [Fact]
    public void Viewport_request_round_trips_through_the_wire_context()
    {
        var request = new ViewportRequest("view-1", 42, ZoomTier.Mid, -1f, -2f, 0f, 3f, 4f, 0f, 5000);

        string json = JsonSerializer.Serialize(request, WireJsonContext.Default.ViewportRequest);
        var back = JsonSerializer.Deserialize(json, WireJsonContext.Default.ViewportRequest);

        Assert.Equal(request, back);
    }

    [Fact]
    public void Tile_key_and_cluster_glyph_round_trip_through_the_wire_context()
    {
        var key = new TileKey(3, 7, -2, 1);
        var glyph = new ClusterGlyph("cluster-9", 128, 1.5f, -0.25f, 0f);

        Assert.Equal(key, JsonSerializer.Deserialize(JsonSerializer.Serialize(key, WireJsonContext.Default.TileKey), WireJsonContext.Default.TileKey));
        Assert.Equal(glyph, JsonSerializer.Deserialize(JsonSerializer.Serialize(glyph, WireJsonContext.Default.ClusterGlyph), WireJsonContext.Default.ClusterGlyph));
    }

    [Fact]
    public void Tile_frame_round_trips_through_the_wire_context()
    {
        float[] xyz = [0f, 1f, 0f, 2f, 3f, 0f];
        var frame = new TileFrame(
            "view-1", 42, 900, new TileKey(2, 1, 0), ZoomTier.Near, 2,
            ["n-1", "n-2"], xyz, [new ClusterGlyph("c-1", 3, 0f, 0f, 0f)]);

        string json = JsonSerializer.Serialize(frame, WireJsonContext.Default.TileFrame);
        var back = JsonSerializer.Deserialize(json, WireJsonContext.Default.TileFrame);

        Assert.NotNull(back);
        Assert.Equal(frame.ViewId, back.ViewId);
        Assert.Equal(frame.Key, back.Key);
        Assert.Equal(frame.NodeIds, back.NodeIds);
        Assert.Equal(xyz, back.Xyz.ToArray());
        Assert.Equal(frame.Clusters, back.Clusters);
    }
}
