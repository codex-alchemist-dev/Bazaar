using Bazaar.Server.Transport.WebTransport;
using Bazaar.Wire.Abstractions;

namespace Bazaar.Wire.Transport.WebTransport.Tests;

/// <summary>Always-on tests: the server handler against in-memory fakes of Kestrel's WebTransport feature (doc 90 section 9).</summary>
public sealed class WebTransportAlwaysTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static WebTransportWireListenerOptions Options(bool enabled = true, bool authorize = true, int pending = 256, string[]? codecs = null, TimeSpan? hello = null) => new()
    {
        Enabled = enabled,
        AdvertisedUrl = "https://example.test:5443/wt",
        Codecs = codecs ?? ["json", "cbor"],
        MaxPendingHandshakes = pending,
        HelloTimeout = hello ?? TimeSpan.FromSeconds(5),
        Authorize = authorize ? _ => ValueTask.FromResult(true) : null,
    };

    private static async Task<WebTransportWireListener> StartAsync(WebTransportWireListenerOptions options)
    {
        WebTransportEndpointExtensions.EnableKestrelWebTransport(options);
        var listener = new WebTransportWireListener(options);
        await listener.StartAsync(Ct);
        return listener;
    }

    [Fact]
    public async Task Off_by_default_is_unsupported_and_offers_nothing()
    {
        using var listener = new WebTransportWireListener(new WebTransportWireListenerOptions { AdvertisedUrl = "https://x/wt" });
        Assert.False(listener.IsSupported);
        Assert.Empty(listener.Offers);
        await Assert.ThrowsAsync<PlatformNotSupportedException>(async () => await listener.StartAsync(Ct));
    }

    [Fact]
    public async Task Enabled_listener_publishes_one_webtransport_offer()
    {
        using var listener = await StartAsync(Options());
        var offer = Assert.Single(listener.Offers);
        Assert.Equal(WireNames.WebTransport, offer.Transport);
        Assert.Equal(["json", "cbor"], offer.Codecs);
    }

    [Fact]
    public async Task Request_without_credential_check_is_refused_with_401()
    {
        using var listener = await StartAsync(Options(authorize: false));
        var context = Requests.Connect(new FakeSession(new FakeStreamPair().Server));
        await listener.HandleAsync(context);
        Assert.Equal(401, context.Response.StatusCode);
    }

    [Fact]
    public async Task Plain_http_request_is_404()
    {
        using var listener = await StartAsync(Options());
        var context = Requests.Connect(new FakeSession(null), isWebTransport: false);
        await listener.HandleAsync(context);
        Assert.Equal(404, context.Response.StatusCode);
    }

    [Fact]
    public async Task Frames_flow_both_ways_with_the_chosen_codec()
    {
        using var listener = await StartAsync(Options());
        var pair = new FakeStreamPair();
        var session = new FakeSession(pair.Server);
        var handler = Task.Run(() => listener.HandleAsync(Requests.Connect(session)), Ct);

        var codec = await WirePrefaceHandshake.ClientAsync(pair.Client, ["cbor", "json"], Ct);
        Assert.Equal("cbor", codec);
        await using var client = new StreamWireConnection(pair.Client, new WireProfile(WireNames.WebTransport, codec), new StreamWireConnectionOptions { PreHelloMaxBytes = int.MaxValue, PreHelloTimeout = null });
        var server = await listener.Accepted.ReadAsync(Ct);
        Assert.Equal(new WireProfile(WireNames.WebTransport, "cbor"), server.Profile);

        await client.SendAsync(new byte[] { 1, 2, 3 }, Ct);
        Assert.Equal(new byte[] { 1, 2, 3 }, (await server.ReceiveAsync(Ct)).Frame.ToArray());
        var big = new byte[100_000];
        new Random(5).NextBytes(big);
        var sending = server.SendAsync(big, Ct).AsTask();
        Assert.Equal(big, (await client.ReceiveAsync(Ct)).Frame.ToArray());
        await sending;

        await server.DisposeAsync();
        await handler.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    [Fact]
    public async Task Unknown_codec_aborts_the_session_with_4400()
    {
        using var listener = await StartAsync(Options(codecs: ["json"]));
        var pair = new FakeStreamPair();
        var session = new FakeSession(pair.Server);
        var handler = Task.Run(() => listener.HandleAsync(Requests.Connect(session)), Ct);
        await pair.Client.WriteAsync(WirePreface.Encode(["msgpack"]), Ct);
        await pair.Client.FlushAsync(Ct);
        await handler.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(StreamWireConnection.BadFrame, session.AbortedWith);
    }

    [Fact]
    public async Task Silent_client_is_aborted_with_4408_after_the_hello_timeout()
    {
        using var listener = await StartAsync(Options(hello: TimeSpan.FromMilliseconds(300)));
        var session = new FakeSession(new FakeStreamPair().Server);
        await listener.HandleAsync(Requests.Connect(session)).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(StreamWireConnection.HelloTimeout, session.AbortedWith);
    }

    [Fact]
    public async Task Too_many_pending_handshakes_get_429()
    {
        using var listener = await StartAsync(Options(pending: 1));
        var first = Task.Run(() => listener.HandleAsync(Requests.Connect(new FakeSession(new FakeStreamPair().Server))), Ct);
        await Task.Delay(300, Ct);
        var context = Requests.Connect(new FakeSession(null));
        await listener.HandleAsync(context);
        Assert.Equal(429, context.Response.StatusCode);
        await listener.StopAsync(Ct);
        await first.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    [Fact]
    public async Task Stop_completes_the_accepted_channel()
    {
        using var listener = await StartAsync(Options());
        await listener.StopAsync(Ct);
        Assert.True(listener.Accepted.Completion.IsCompleted);
        Assert.Empty(listener.Offers);
    }
}
