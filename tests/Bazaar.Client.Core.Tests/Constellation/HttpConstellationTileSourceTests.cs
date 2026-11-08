using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using Bazaar.Client.Core.Constellation;
using Bazaar.Client.Core.Server;
using Bazaar.Contracts.Graph;

namespace Bazaar.Client.Core.Tests.Constellation;

public class HttpConstellationTileSourceTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            return respond(request);
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("connection refused");
    }

    private static readonly ViewportRequest _request = new(
        viewId: "constellation",
        graphVersion: 7,
        tier: Bazaar.Contracts.Graph.ZoomTier.Near,
        minX: -1.5f,
        minY: 2f,
        minZ: 0,
        maxX: 10.25f,
        maxY: 20f,
        maxZ: 0,
        maxNodes: 500);

    private static HttpConstellationTileSource Source(HttpMessageHandler handler, string? token = "secret")
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080/") };
        return new HttpConstellationTileSource(http, () => token);
    }

    private static HttpResponseMessage Binary(byte[] body) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) { Headers = { ContentType = new("application/x-bazaar-tile") } } };

    // Builds a tile body by the layout in the server's TileFrameBinaryCodec: magic, dimensions, tier, view id, graph version,
    // layout sequence, tile key, node ids, packed x,y,z, then the cluster list. Little-endian.
    private static byte[] TileBody(string viewId, long graphVersion, string[] ids, float[] xyz)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("BZT1"u8.ToArray());
            writer.Write((byte)2);
            writer.Write((byte)Bazaar.Contracts.Graph.ZoomTier.Near);
            writer.Write(viewId);
            writer.Write(graphVersion);
            writer.Write(42L);
            writer.Write(3);
            writer.Write(1);
            writer.Write(2);
            writer.Write(0);
            writer.Write(ids.Length);
            foreach (var id in ids) writer.Write(id);
            foreach (var value in xyz) writer.Write(value);
            writer.Write(0);
        }

        return stream.ToArray();
    }

    [Fact]
    public async Task Posts_the_box_to_the_tile_route_with_the_bearer_token()
    {
        var handler = new FakeHandler(_ => Binary(TileBody("constellation", 7, ["a"], [1f, 2f, 0f])));
        var source = Source(handler);

        await source.GetFrameAsync(_request, TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/constellation/tiles", request.RequestUri!.AbsolutePath);
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("secret", request.Headers.Authorization.Parameter);

        using var json = JsonDocument.Parse(Assert.Single(handler.Bodies));
        var root = json.RootElement;
        Assert.Equal("constellation", root.GetProperty("view_id").GetString());
        Assert.Equal(7, root.GetProperty("graph_version").GetInt64());
        Assert.Equal("Near", root.GetProperty("tier").GetString());
        Assert.Equal(-1.5f, root.GetProperty("min_x").GetSingle());
        Assert.Equal(2f, root.GetProperty("min_y").GetSingle());
        Assert.Equal(0f, root.GetProperty("min_z").GetSingle());
        Assert.Equal(10.25f, root.GetProperty("max_x").GetSingle());
        Assert.Equal(20f, root.GetProperty("max_y").GetSingle());
        Assert.Equal(0f, root.GetProperty("max_z").GetSingle());
        Assert.Equal(500, root.GetProperty("max_nodes").GetInt32());
    }

    [Fact]
    public async Task Without_a_credential_sends_no_authorization_header()
    {
        var handler = new FakeHandler(_ => Binary(TileBody("constellation", 7, ["a"], [1f, 2f, 0f])));

        await Source(handler, token: null).GetFrameAsync(_request, TestContext.Current.CancellationToken);

        Assert.Null(Assert.Single(handler.Requests).Headers.Authorization);
    }

    [Fact]
    public async Task A_400_answer_returns_null()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("bad_request", Encoding.UTF8, "application/json"),
        });

        var frame = await Source(handler).GetFrameAsync(_request, TestContext.Current.CancellationToken);

        Assert.Null(frame);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_binary_200_decodes_to_the_tile_frame()
    {
        var handler = new FakeHandler(_ => Binary(TileBody("constellation", 7, ["a", "bee"], [1f, 2f, 0f, 3.5f, -4f, 0f])));

        var frame = await Source(handler).GetFrameAsync(_request, TestContext.Current.CancellationToken);

        Assert.NotNull(frame);
        Assert.Equal("constellation", frame.ViewId);
        Assert.Equal(7, frame.GraphVersion);
        Assert.Equal(42, frame.LayoutSeq);
        Assert.Equal(Bazaar.Contracts.Graph.ZoomTier.Near, frame.Tier);
        Assert.Equal(2, frame.Dimensions);
        Assert.Equal(["a", "bee"], frame.NodeIds);
        Assert.Equal([1f, 2f, 0f, 3.5f, -4f, 0f], frame.Xyz.ToArray());
        Assert.Empty(frame.Clusters);
    }

    [Fact]
    public async Task A_200_with_a_malformed_body_returns_null()
    {
        var handler = new FakeHandler(_ => Binary("not a tile"u8.ToArray()));

        Assert.Null(await Source(handler).GetFrameAsync(_request, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_200_with_another_content_type_returns_null()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        });

        Assert.Null(await Source(handler).GetFrameAsync(_request, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_transport_failure_returns_null()
    {
        var source = Source(new ThrowingHandler());

        Assert.Null(await source.GetFrameAsync(_request, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Create_sends_to_the_endpoint_base_address_and_credential()
    {
        var handler = new FakeHandler(_ => Binary(TileBody("constellation", 7, ["a"], [1f, 2f, 0f])));
        var endpoint = new ServerEndpoint(new Uri("http://127.0.0.1:9090/"), "paired-token");
        var source = HttpConstellationTileSource.Create(endpoint, handler);

        await source.GetFrameAsync(_request, TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("127.0.0.1", request.RequestUri!.Host);
        Assert.Equal(9090, request.RequestUri.Port);
        Assert.Equal("/constellation/tiles", request.RequestUri.AbsolutePath);
        Assert.Equal("paired-token", request.Headers.Authorization!.Parameter);
    }
}
