namespace Bazaar.Wire.Abstractions.Tests;

public sealed class WirePrefaceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Preface_round_trips_and_consumes_only_its_bytes()
    {
        var bytes = WirePreface.Encode(["cbor", "json"]).Concat(new byte[] { 7, 7 }).ToArray();
        Assert.Equal([0xBA, 0x5A, 9], bytes[..3]);
        using var stream = new MemoryStream(bytes);
        Assert.Equal(["cbor", "json"], await WirePreface.ReadAsync(stream, Ct));
        Assert.Equal(bytes.Length - 2, stream.Position);
    }

    [Theory]
    [InlineData(new byte[] { 0x00, 0x5A, 1, (byte)'a' })]
    [InlineData(new byte[] { 0xBA, 0x5B, 1, (byte)'a' })]
    [InlineData(new byte[] { 0xBA, 0x5A, 0 })]
    [InlineData(new byte[] { 0xBA, 0x5A, 2, (byte)'a', (byte)',' })]
    [InlineData(new byte[] { 0xBA, 0x5A, 1, 0xFF })]
    public async Task Malformed_prefaces_are_rejected(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await WirePreface.ReadAsync(stream, Ct));
    }

    [Fact]
    public async Task Truncated_preface_ends_the_stream()
    {
        using var stream = new MemoryStream([0xBA, 0x5A, 5, (byte)'j']);
        await Assert.ThrowsAsync<EndOfStreamException>(async () => await WirePreface.ReadAsync(stream, Ct));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a,b")]
    [InlineData("a b")]
    public void Encode_rejects_bad_names(string name) => Assert.Throws<ArgumentException>(() => WirePreface.Encode([name]));

    [Fact]
    public void Encode_rejects_empty_and_overlong_lists()
    {
        Assert.Throws<ArgumentException>(() => WirePreface.Encode([]));
        Assert.Throws<ArgumentException>(() => WirePreface.Encode([new string('x', 256)]));
    }

    [Fact]
    public async Task Handshake_picks_the_first_client_codec_the_operator_enabled()
    {
        var (a, b) = MemoryDuplex.Create();
        var server = WirePrefaceHandshake.ServerAsync(b, ["json", "cbor"], TimeSpan.FromSeconds(5), Ct).AsTask();
        var client = await WirePrefaceHandshake.ClientAsync(a, ["msgpack", "cbor", "json"], Ct);
        Assert.Equal("cbor", client);
        Assert.Equal("cbor", await server);
    }

    [Fact]
    public async Task Handshake_with_no_common_codec_yields_null()
    {
        var (a, b) = MemoryDuplex.Create();
        await a.WriteAsync(WirePreface.Encode(["cbor"]), Ct);
        Assert.Null(await WirePrefaceHandshake.ServerAsync(b, ["json"], TimeSpan.FromSeconds(5), Ct));
    }

    [Fact]
    public async Task Handshake_bad_magic_throws_and_silence_times_out()
    {
        var (a, b) = MemoryDuplex.Create();
        await a.WriteAsync(new byte[] { 1, 2, 3 }, Ct);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await WirePrefaceHandshake.ServerAsync(b, ["json"], TimeSpan.FromSeconds(5), Ct));

        var (_, d) = MemoryDuplex.Create();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await WirePrefaceHandshake.ServerAsync(d, ["json"], TimeSpan.FromMilliseconds(100), Ct));
    }

    [Fact]
    public async Task Client_rejects_a_codec_it_did_not_offer()
    {
        var (a, b) = MemoryDuplex.Create();
        await b.WriteAsync(WirePreface.Encode(["cbor"]), Ct);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await WirePrefaceHandshake.ClientAsync(a, ["json"], Ct));
    }

    [Fact]
    public void ParseEndpoint_handles_hosts_and_ipv6()
    {
        Assert.Equal(("example.test", 4433), QuicWireSupport.ParseEndpoint("example.test:4433"));
        Assert.Equal(("::1", 4433), QuicWireSupport.ParseEndpoint("[::1]:4433"));
        Assert.Throws<FormatException>(() => QuicWireSupport.ParseEndpoint("nohost"));
        Assert.Throws<FormatException>(() => QuicWireSupport.ParseEndpoint("h:0"));
    }
}
