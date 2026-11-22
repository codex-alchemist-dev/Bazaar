using System.Text;
using Bazaar.Client.Transport.AndroidHttp3;
using Bazaar.Client.Transport.AndroidHttp3.Android;
using Bazaar.Wire.Abstractions;

namespace Bazaar.Client.Transport.AndroidHttp3.Android.Tests;

public sealed class TransportOverAdapterTests
{
    private sealed class Recorder : IWireConnectionObserver
    {
        public List<string> Protocols { get; } = [];

        public List<WireNetworkEvent> Events { get; } = [];

        public List<WireReconnectReason> Reconnects { get; } = [];

        public void OnNegotiated(string protocol) => Protocols.Add(protocol);

        public void OnNetworkEvent(WireNetworkEvent networkEvent) => Events.Add(networkEvent);

        public void OnReconnectRequired(WireReconnectReason reason, WireNetworkEvent? lastNetworkEvent) => Reconnects.Add(reason);
    }

    [Fact]
    public async Task ConnectSendReceiveAndHandoverThenCutReportsToTheSharedObserver()
    {
        var ct = TestContext.Current.CancellationToken;
        var bridge = new FakeBridge { AutoReady = false };
        var observer = new Recorder();
        var transport = new CronetHttp3Transport(new JavaCronetEngine(bridge), new CronetHttp3Options { Observer = observer });
        var offer = new TransportOffer(WireNames.Grpc, "bazaar.example:443", "h2,h3", ["json"]);

        var connecting = transport.ConnectAsync(offer, ["json"], ct).AsTask();
        while (bridge.Stream is null) await Task.Yield();
        bridge.Stream.Callback.OnStreamReady();
        bridge.Stream.Callback.OnResponseHeaders(200, "h3", new Dictionary<string, string> { ["bazaar-codec"] = "json" });
        await using var connection = await connecting;
        Assert.Equal(["h3"], observer.Protocols);

        await connection.SendAsync(Encoding.ASCII.GetBytes("hi"), ct);
        Assert.Equal(new byte[] { 0, 0, 0, 0, 4, 0x0A, 2, (byte)'h', (byte)'i' }, Assert.Single(bridge.Stream.Writes).Data);

        bridge.Raise(new WireNetworkEvent(WireNetworkEventKind.Handover, DateTimeOffset.UnixEpoch, "wifi to cell"));
        Assert.Equal(WireNetworkEventKind.Handover, Assert.Single(observer.Events).Kind);

        var receive = connection.ReceiveAsync(ct).AsTask();
        bridge.Stream.Callback.OnReadCompleted(ReadOnlyMemory<byte>.Empty, endOfStream: true);
        bridge.Stream.Callback.OnSucceeded();
        Assert.True((await receive).IsClosed);
        Assert.Equal([WireReconnectReason.StreamCut], observer.Reconnects);
    }
}
