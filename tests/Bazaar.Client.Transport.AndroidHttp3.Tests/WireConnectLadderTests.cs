using Bazaar.Wire.Abstractions;

namespace Bazaar.Client.Transport.AndroidHttp3.Tests;

public sealed class WireConnectLadderTests
{
    private static readonly string[] _order = ["grpc", "ws"];
    private static readonly string[] _json = ["json"];
    private static readonly TransportOffer[] _offers =
    [
        new("ws", "wss://bazaar.example/ws", null, ["json"]),
        new("grpc", "bazaar.example:443", "h2,h3", ["json"]),
    ];

    private static WireConnectLadder Ladder(IWireConnectionObserver observer, params IWireTransportConnector[] connectors) => new(connectors, _order, observer);

    [Fact]
    public async Task Cronet_goes_first_when_it_works()
    {
        var engine = new FakeCronetEngine();
        var cronet = new CronetHttp3Transport(engine);
        var h2 = new FakeConnector("grpc");
        var ws = new FakeConnector("ws");
        await using var connection = await Ladder(new RecordingObserver(), cronet, h2, ws).ConnectAsync(_offers, _json, CancellationToken.None);
        Assert.Single(engine.Requests);
        Assert.Equal(0, h2.Calls);
        Assert.Equal(0, ws.Calls);
        Assert.Equal("grpc", connection.Profile.Transport);
    }

    [Fact]
    public async Task Unavailable_cronet_drops_out_and_grpc_h2_serves()
    {
        var engine = new FakeCronetEngine { IsAvailable = false };
        var h2 = new FakeConnector("grpc");
        await using var connection = await Ladder(new RecordingObserver(), new CronetHttp3Transport(engine), h2, new FakeConnector("ws")).ConnectAsync(_offers, _json, CancellationToken.None);
        Assert.Empty(engine.Requests);
        Assert.Equal(1, h2.Calls);
    }

    [Fact]
    public async Task H3_blocked_falls_to_grpc_h2_and_is_observed()
    {
        var engine = new FakeCronetEngine { OpenFailure = new IOException("QUIC_HANDSHAKE_TIMEOUT") };
        var observer = new RecordingObserver();
        var h2 = new FakeConnector("grpc");
        await using var connection = await Ladder(observer, new CronetHttp3Transport(engine), h2, new FakeConnector("ws")).ConnectAsync(_offers, _json, CancellationToken.None);
        Assert.Equal(1, h2.Calls);
        Assert.Equal("AndroidHttp3", Assert.Single(observer.Fallbacks).From);
    }

    [Fact]
    public async Task Cronet_and_h2_failing_fall_to_ws()
    {
        var engine = new FakeCronetEngine { OpenFailure = new IOException("blocked") };
        var observer = new RecordingObserver();
        var ws = new FakeConnector("ws");
        await using var connection = await Ladder(observer, new CronetHttp3Transport(engine), new FakeConnector("grpc", new IOException("h2 blocked")), ws).ConnectAsync(_offers, _json, CancellationToken.None);
        Assert.Equal(1, ws.Calls);
        Assert.Equal("ws", connection.Profile.Transport);
        Assert.Equal(["AndroidHttp3", "grpc"], observer.Fallbacks.Select(f => f.From));
    }

    [Fact]
    public async Task Refusal_stops_the_walk()
    {
        var engine = new FakeCronetEngine();
        engine.Stream.Head = new CronetResponseHead(200, "h3", new Dictionary<string, string> { ["grpc-status"] = "16" });
        var h2 = new FakeConnector("grpc");
        await Assert.ThrowsAsync<WireConnectRefusedException>(async () =>
            await Ladder(new RecordingObserver(), new CronetHttp3Transport(engine), h2).ConnectAsync(_offers, _json, CancellationToken.None));
        Assert.Equal(0, h2.Calls);
    }

    [Fact]
    public async Task Every_rung_failing_throws_with_all_failures()
    {
        var engine = new FakeCronetEngine { IsAvailable = true, OpenFailure = new IOException("a") };
        var ex = await Assert.ThrowsAsync<IOException>(async () =>
            await Ladder(new RecordingObserver(), new CronetHttp3Transport(engine), new FakeConnector("grpc", new IOException("b")), new FakeConnector("ws", new IOException("c"))).ConnectAsync(_offers, _json, CancellationToken.None));
        Assert.Equal(3, Assert.IsType<AggregateException>(ex.InnerException).InnerExceptions.Count);
    }
}
