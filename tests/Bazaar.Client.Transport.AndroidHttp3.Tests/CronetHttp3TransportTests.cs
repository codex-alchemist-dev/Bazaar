using System.Text;
using Bazaar.Wire.Abstractions;

namespace Bazaar.Client.Transport.AndroidHttp3.Tests;

public sealed class CronetHttp3TransportTests
{
    private static readonly TransportOffer _offer = new(WireNames.Grpc, "bazaar.example:443", "h2,h3", ["json", "cbor"]);
    private static readonly string[] _json = ["json"];

    private static (CronetHttp3Transport Transport, FakeCronetEngine Engine, RecordingObserver Observer) Make(int maxFrame = 256 * 1024)
    {
        var engine = new FakeCronetEngine();
        var observer = new RecordingObserver();
        var transport = new CronetHttp3Transport(engine, new CronetHttp3Options
        {
            Observer = observer,
            MaxFrameBytes = maxFrame,
            DeviceId = "dev-1",
            CredentialProvider = _ => ValueTask.FromResult<string?>("fake-token-for-tests"),
        });
        return (transport, engine, observer);
    }

    [Fact]
    public async Task Connect_sends_grpc_headers_and_codec_and_no_early_data()
    {
        var (transport, engine, observer) = Make();
        await using var connection = await transport.ConnectAsync(_offer, ["json", "cbor"], CancellationToken.None);

        var request = Assert.Single(engine.Requests);
        Assert.Equal("https://bazaar.example/bazaar.wire.v1.Session/Connect", request.Url.ToString());
        Assert.False(request.AllowEarlyData);
        var headers = request.Headers.ToDictionary(h => h.Key, h => h.Value);
        Assert.Equal("application/grpc", headers["content-type"]);
        Assert.Equal("trailers", headers["te"]);
        Assert.Equal("json,cbor", headers["bazaar-codec"]);
        Assert.Equal("Bearer fake-token-for-tests", headers["authorization"]);
        Assert.Equal("dev-1", headers["bazaar-device"]);
        Assert.Equal(new WireProfile("grpc", "json"), connection.Profile);
        Assert.Equal(["h3"], observer.Protocols);
    }

    [Fact]
    public async Task Frames_use_grpc_framing_both_ways()
    {
        var (transport, engine, _) = Make();
        await using var connection = await transport.ConnectAsync(_offer, _json, CancellationToken.None);

        await connection.SendAsync(Encoding.ASCII.GetBytes("hello"), CancellationToken.None);
        Assert.True(engine.Stream.Written.TryDequeue(out var sent));
        Assert.Equal(new byte[] { 0, 0, 0, 0, 7, 0x0A, 5, (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o' }, sent);

        // Reply split across reads to cover partial reads.
        var reply = GrpcMessageFraming.Encode(Encoding.ASCII.GetBytes("world"));
        engine.Stream.Feed(reply[..3]);
        engine.Stream.Feed(reply[3..]);
        var received = await connection.ReceiveAsync(CancellationToken.None);
        Assert.False(received.IsClosed);
        Assert.Equal("world", Encoding.ASCII.GetString(received.Frame.Span));
    }

    [Fact]
    public async Task Send_over_the_limit_is_refused()
    {
        var (transport, _, _) = Make(maxFrame: 8);
        await using var connection = await transport.ConnectAsync(_offer, _json, CancellationToken.None);
        await Assert.ThrowsAsync<ArgumentException>(async () => await connection.SendAsync(new byte[9], CancellationToken.None));
    }

    [Fact]
    public async Task Declared_length_over_the_limit_closes_4400_before_the_body_is_read()
    {
        var (transport, engine, _) = Make(maxFrame: 8);
        await using var connection = await transport.ConnectAsync(_offer, _json, CancellationToken.None);
        // Prefix only: declares 1 GiB; nothing else is ever fed, so a reader that waited for the body would hang.
        engine.Stream.Feed([0, 0x40, 0, 0, 0]);
        var received = await connection.ReceiveAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(4400, received.CloseCode);
    }

    [Fact]
    public async Task Data_field_over_the_limit_and_compressed_messages_close_4400()
    {
        var (transport, engine, _) = Make(maxFrame: 8);
        await using var connection = await transport.ConnectAsync(_offer, _json, CancellationToken.None);
        // Message length (<= 8 + 16) is fine, but the data field claims 9 bytes.
        var message = GrpcMessageFraming.Encode(new byte[9]);
        engine.Stream.Feed(message);
        Assert.Equal(4400, (await connection.ReceiveAsync(CancellationToken.None)).CloseCode);

        var (transport2, engine2, _) = Make();
        await using var second = await transport2.ConnectAsync(_offer, _json, CancellationToken.None);
        var compressed = GrpcMessageFraming.Encode([1]);
        compressed[0] = 1;
        engine2.Stream.Feed(compressed);
        Assert.Equal(4400, (await second.ReceiveAsync(CancellationToken.None)).CloseCode);
    }

    [Fact]
    public async Task Clean_end_reports_the_close_code_from_trailers()
    {
        var (transport, engine, observer) = Make();
        await using var connection = await transport.ConnectAsync(_offer, _json, CancellationToken.None);
        engine.Stream.TrailersValue["bazaar-close-code"] = "4403";
        engine.Stream.EndBody();
        Assert.Equal(4403, (await connection.ReceiveAsync(CancellationToken.None)).CloseCode);
        Assert.Empty(observer.Reconnects);
    }

    [Fact]
    public async Task Clean_end_maps_grpc_status_when_no_close_code()
    {
        var (transport, engine, _) = Make();
        await using var connection = await transport.ConnectAsync(_offer, _json, CancellationToken.None);
        engine.Stream.TrailersValue["grpc-status"] = "16";
        engine.Stream.EndBody();
        Assert.Equal(4401, (await connection.ReceiveAsync(CancellationToken.None)).CloseCode);
    }

    [Fact]
    public async Task Handover_is_observed_and_a_cut_stream_signals_reconnect_with_it()
    {
        var (transport, engine, observer) = Make();
        await using var connection = await transport.ConnectAsync(_offer, _json, CancellationToken.None);
        var handover = new WireNetworkEvent(WireNetworkEventKind.Handover, DateTimeOffset.UnixEpoch, "wifi to cell");
        engine.Stream.Raise(handover);
        Assert.Equal([handover], observer.Events);

        engine.Stream.CleanEnd = false;
        engine.Stream.EndBody();
        var received = await connection.ReceiveAsync(CancellationToken.None);
        Assert.Equal(0, received.CloseCode);
        var reconnect = Assert.Single(observer.Reconnects);
        Assert.Equal(WireReconnectReason.StreamCut, reconnect.Reason);
        Assert.Equal(handover, reconnect.Last);
    }

    [Fact]
    public async Task Failed_read_and_write_signal_reconnect()
    {
        var (transport, engine, observer) = Make();
        await using var connection = await transport.ConnectAsync(_offer, _json, CancellationToken.None);
        engine.Stream.Raise(new WireNetworkEvent(WireNetworkEventKind.Suspended, DateTimeOffset.UnixEpoch));
        engine.Stream.FailReads = true;
        var received = await connection.ReceiveAsync(CancellationToken.None);
        Assert.Equal(0, received.CloseCode);
        Assert.Equal(WireReconnectReason.StreamFailed, Assert.Single(observer.Reconnects).Reason);
        // Closed stays closed.
        Assert.True((await connection.ReceiveAsync(CancellationToken.None)).IsClosed);
    }

    [Fact]
    public async Task Dispose_completes_writes_and_releases_the_stream()
    {
        var (transport, engine, _) = Make();
        var connection = await transport.ConnectAsync(_offer, _json, CancellationToken.None);
        await connection.DisposeAsync();
        Assert.True(engine.Stream.WritesCompleted);
        Assert.True(engine.Stream.Disposed);
    }

    [Fact]
    public async Task Unavailable_engine_is_unsupported_and_asks_for_fallback()
    {
        var (transport, engine, _) = Make();
        engine.IsAvailable = false;
        Assert.False(transport.IsSupported);
        await Assert.ThrowsAsync<WireFallbackException>(async () => await transport.ConnectAsync(_offer, _json, CancellationToken.None));
    }

    [Fact]
    public async Task H3_blocked_cases_ask_for_fallback_and_free_the_stream()
    {
        var (transport, engine, _) = Make();
        engine.Stream.Head = new CronetResponseHead(200, "h2", new Dictionary<string, string> { ["bazaar-codec"] = "json" });
        await Assert.ThrowsAsync<WireFallbackException>(async () => await transport.ConnectAsync(_offer, _json, CancellationToken.None));
        Assert.True(engine.Stream.Disposed);

        var (blocked, blockedEngine, _) = Make();
        blockedEngine.OpenFailure = new IOException("QUIC_HANDSHAKE_TIMEOUT");
        await Assert.ThrowsAsync<WireFallbackException>(async () => await blocked.ConnectAsync(_offer, _json, CancellationToken.None));

        var (plain, _, _) = Make();
        await Assert.ThrowsAsync<WireFallbackException>(async () => await plain.ConnectAsync(_offer with { Endpoint = "http://bazaar.example" }, _json, CancellationToken.None));
    }

    [Fact]
    public async Task Refusal_is_not_a_fallback()
    {
        var (transport, engine, _) = Make();
        engine.Stream.Head = new CronetResponseHead(200, "h3", new Dictionary<string, string> { ["grpc-status"] = "16" });
        var ex = await Assert.ThrowsAsync<WireConnectRefusedException>(async () => await transport.ConnectAsync(_offer, _json, CancellationToken.None));
        Assert.Equal(4401, ex.CloseCode);
    }

    [Fact]
    public async Task Codec_the_client_did_not_offer_is_rejected()
    {
        var (transport, engine, _) = Make();
        engine.Stream.Head = new CronetResponseHead(200, "h3", new Dictionary<string, string> { ["bazaar-codec"] = "cbor" });
        await Assert.ThrowsAsync<InvalidDataException>(async () => await transport.ConnectAsync(_offer, _json, CancellationToken.None));
    }

    [Fact]
    public void Framing_round_trips_including_empty_and_large_frames()
    {
        foreach (var size in new[] { 0, 1, 127, 128, 16384, 70000 })
        {
            var frame = new byte[size];
            new Random(size).NextBytes(frame);
            var message = GrpcMessageFraming.Encode(frame);
            Assert.True(GrpcMessageFraming.TryReadPrefix(message.AsSpan(0, 5), 1 << 20, out var length));
            Assert.Equal(message.Length - 5, length);
            Assert.True(GrpcMessageFraming.TryDecode(message.AsSpan(5), 1 << 20, out var decoded));
            Assert.Equal(frame, decoded);
        }
    }

    [Fact]
    public void Malformed_messages_are_rejected_not_thrown()
    {
        Assert.False(GrpcMessageFraming.TryDecode([0x0A, 0x05, 1], 100, out _));
        Assert.False(GrpcMessageFraming.TryDecode([0x0B], 100, out _));
        Assert.False(GrpcMessageFraming.TryDecode([0x0A, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01], 100, out _));
        Assert.True(GrpcMessageFraming.TryDecode([0x10, 0x01], 100, out var frame));
        Assert.Empty(frame);
    }
}
