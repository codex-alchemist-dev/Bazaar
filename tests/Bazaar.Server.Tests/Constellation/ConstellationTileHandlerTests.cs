using System.Text;
using Bazaar.Contracts.Graph;
using Bazaar.Server.Constellation.Tiles;

namespace Bazaar.Server.Tests.Constellation;

/// <summary>Tile codec, handler and fake source (doc 19 tile feed). Pure: no database, no sockets.</summary>
public sealed class ConstellationTileHandlerTests
{
    private static readonly FakeTileNode[] _nodes =
    [
        new("a", 0f, 0f, 0f),
        new("b", 1f, 1f, 0f),
        new("c", 5f, 5f, 0f),
        new("d", 2f, -1f, 0f),
    ];

    private static readonly float[] _sampleXyz = [0f, 1f, 2f, 3f, 4f, 5f];
    private static readonly string[] _insideIds = ["a", "b", "d"];
    private static readonly string[] _firstTwoIds = ["a", "b"];

    private static ViewportRequest Box(long graphVersion = 7, int maxNodes = 100, float max = 3f) =>
        new("view-1", graphVersion, ZoomTier.Mid, -10f, -10f, 0f, max, max, 0f, maxNodes);

    private static TileFrame SampleFrame() => new(
        "view-1", 7, 42, new TileKey(2, 1, 0), ZoomTier.Near, 3,
        ["n-1", "n-2"],
        _sampleXyz,
        [new ClusterGlyph("c-1", 9, 1.5f, -0.5f, 2f)]);

    [Fact]
    public void Codec_round_trips_a_frame_with_nodes_and_clusters()
    {
        TileFrame frame = SampleFrame();

        TileFrame back = TileFrameBinaryCodec.Decode(TileFrameBinaryCodec.Encode(frame));

        Assert.Equal(frame.ViewId, back.ViewId);
        Assert.Equal(frame.GraphVersion, back.GraphVersion);
        Assert.Equal(frame.LayoutSeq, back.LayoutSeq);
        Assert.Equal(frame.Key, back.Key);
        Assert.Equal(frame.Tier, back.Tier);
        Assert.Equal(frame.Dimensions, back.Dimensions);
        Assert.Equal(frame.NodeIds, back.NodeIds);
        Assert.Equal(frame.Xyz.ToArray(), back.Xyz.ToArray());
        Assert.Equal(frame.Clusters, back.Clusters);
    }

    [Fact]
    public void Codec_round_trips_an_empty_frame()
    {
        var frame = new TileFrame("v", 1, 1, new TileKey(0, 0, 0), ZoomTier.Far, 2, [], ReadOnlyMemory<float>.Empty, []);

        TileFrame back = TileFrameBinaryCodec.Decode(TileFrameBinaryCodec.Encode(frame));

        Assert.Empty(back.NodeIds);
        Assert.Empty(back.Clusters);
        Assert.Equal(0, back.Xyz.Length);
    }

    [Fact]
    public void Codec_rejects_a_payload_with_the_wrong_magic()
    {
        byte[] payload = TileFrameBinaryCodec.Encode(SampleFrame());
        payload[0] = (byte)'X';

        Assert.Throws<InvalidDataException>(() => TileFrameBinaryCodec.Decode(payload));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(20)]
    public void Codec_rejects_a_truncated_payload(int keep)
    {
        byte[] payload = TileFrameBinaryCodec.Encode(SampleFrame());
        byte[] cut = payload[..keep];

        Assert.Throws<InvalidDataException>(() => TileFrameBinaryCodec.Decode(cut));
    }

    [Fact]
    public void Codec_rejects_trailing_bytes()
    {
        byte[] payload = TileFrameBinaryCodec.Encode(SampleFrame());
        byte[] extended = [.. payload, 0];

        Assert.Throws<InvalidDataException>(() => TileFrameBinaryCodec.Decode(extended));
    }

    [Fact]
    public void Codec_rejects_a_node_count_that_cannot_fit_in_the_payload()
    {
        byte[] payload = TileFrameBinaryCodec.Encode(SampleFrame());
        // The node count sits right after the header: magic(4) + dims(1) + tier(1) + viewId string + version(8) +
        // layout seq(8) + key(16). Overwrite it with a huge value.
        int countOffset = 4 + 1 + 1 + 1 + Encoding.UTF8.GetByteCount("view-1") + 8 + 8 + 16;
        BitConverter.GetBytes(int.MaxValue).CopyTo(payload, countOffset);

        Assert.Throws<InvalidDataException>(() => TileFrameBinaryCodec.Decode(payload));
    }

    [Fact]
    public async Task Handler_returns_a_decodable_binary_frame_for_a_matching_graph_version()
    {
        var handler = new ConstellationTileHandler(new FakeConstellationTileSource(7, _nodes), nodeCap: 50_000);

        TileResponse response = await handler.HandleAsync(Box(), TestContext.Current.CancellationToken);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(ConstellationTileHandler.BinaryContentType, response.ContentType);
        TileFrame frame = TileFrameBinaryCodec.Decode(response.Body);
        Assert.Equal(_insideIds, frame.NodeIds);
    }

    [Fact]
    public async Task Handler_refuses_a_frame_from_another_graph_version()
    {
        var handler = new ConstellationTileHandler(new FakeConstellationTileSource(8, _nodes), nodeCap: 50_000);

        TileResponse response = await handler.HandleAsync(Box(graphVersion: 7), TestContext.Current.CancellationToken);

        Assert.Equal(409, response.StatusCode);
        Assert.Equal("text/plain; charset=utf-8", response.ContentType);
    }

    [Fact]
    public async Task Handler_returns_not_found_when_the_source_has_no_frame()
    {
        var handler = new ConstellationTileHandler(new NoFrameSource(), nodeCap: 50_000);

        TileResponse response = await handler.HandleAsync(Box(), TestContext.Current.CancellationToken);

        Assert.Equal(404, response.StatusCode);
    }

    [Fact]
    public async Task Handler_clamps_the_request_to_the_operator_node_cap()
    {
        var handler = new ConstellationTileHandler(new FakeConstellationTileSource(7, _nodes), nodeCap: 2);

        TileResponse response = await handler.HandleAsync(Box(maxNodes: 1000), TestContext.Current.CancellationToken);

        TileFrame frame = TileFrameBinaryCodec.Decode(response.Body);
        Assert.Equal(_firstTwoIds, frame.NodeIds);
    }

    [Fact]
    public void Handler_rejects_a_non_positive_node_cap()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConstellationTileHandler(new NoFrameSource(), nodeCap: 0));
    }

    [Fact]
    public async Task Fake_source_returns_only_the_nodes_inside_the_box()
    {
        var source = new FakeConstellationTileSource(7, _nodes);

        TileFrame? frame = await source.GetFrameAsync(Box(max: 1.5f), TestContext.Current.CancellationToken);

        Assert.NotNull(frame);
        // d sits at x = 2, outside the box's x maximum of 1.5, so only a and b are inside.
        Assert.Equal(_firstTwoIds, frame.NodeIds);
        Assert.Empty(frame.Clusters);
        Assert.Equal(frame.NodeIds.Count * 3, frame.Xyz.Length);
    }

    private sealed class NoFrameSource : IConstellationTileSource
    {
        public Task<TileFrame?> GetFrameAsync(ViewportRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult<TileFrame?>(null);
    }
}
