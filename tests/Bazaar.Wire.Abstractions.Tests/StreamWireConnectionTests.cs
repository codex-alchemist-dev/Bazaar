using System.Buffers;

namespace Bazaar.Wire.Abstractions.Tests;

public sealed class StreamWireConnectionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly WireProfile Quic = new(WireNames.Quic, WireNames.Json);

    private static byte[] Framed(ReadOnlySpan<byte> payload)
    {
        var writer = new ArrayBufferWriter<byte>();
        LengthPrefixFramer.Write(payload, writer, 1 << 20);
        return writer.WrittenSpan.ToArray();
    }

    [Fact]
    public async Task Frames_echo_in_both_directions()
    {
        var (a, b) = MemoryDuplex.Create();
        await using var client = new StreamWireConnection(a, Quic, new StreamWireConnectionOptions { PreHelloMaxBytes = int.MaxValue, PreHelloTimeout = null });
        await using var server = new StreamWireConnection(b, Quic);
        var big = new byte[100_000];
        new Random(1).NextBytes(big);

        await client.SendAsync(new byte[] { 1, 2, 3 }, Ct);
        var sending = client.SendAsync(big, Ct).AsTask();
        Assert.Equal(new byte[] { 1, 2, 3 }, (await server.ReceiveAsync(Ct)).Frame.ToArray());
        Assert.Equal(big, (await server.ReceiveAsync(Ct)).Frame.ToArray());
        await sending;
        var echoing = server.SendAsync(big, Ct).AsTask();
        Assert.Equal(big, (await client.ReceiveAsync(Ct)).Frame.ToArray());
        await echoing;
    }

    [Fact]
    public async Task Fragmented_and_pipelined_frames_decode()
    {
        var (a, b) = MemoryDuplex.Create();
        await using var server = new StreamWireConnection(b, Quic);
        var bytes = Framed([9, 9]).Concat(Framed([8])).Concat(Framed([])).ToArray();
        foreach (var x in bytes) await a.WriteAsync(new[] { x }, Ct);
        Assert.Equal(new byte[] { 9, 9 }, (await server.ReceiveAsync(Ct)).Frame.ToArray());
        Assert.Equal(new byte[] { 8 }, (await server.ReceiveAsync(Ct)).Frame.ToArray());
        var empty = await server.ReceiveAsync(Ct);
        Assert.False(empty.IsClosed);
        Assert.Empty(empty.Frame.ToArray());
    }

    [Fact]
    public async Task Oversized_frame_is_refused_on_the_length_alone_with_4400()
    {
        var (a, b) = MemoryDuplex.Create();
        var closed = new TaskCompletionSource<int>();
        await using var server = new StreamWireConnection(b, Quic, new StreamWireConnectionOptions
        {
            MaxFrameBytes = 1000,
            CloseAsync = code =>
            {
                closed.SetResult(code);
                return ValueTask.CompletedTask;
            },
        });

        // Declares 1 MiB but sends no payload: refusal must not wait for it.
        var header = new ArrayBufferWriter<byte>();
        LengthPrefixFramer.Write(new byte[100_000], header, 1 << 20);
        await a.WriteAsync(header.WrittenMemory[..4], Ct);
        var received = await server.ReceiveAsync(Ct);
        Assert.True(received.IsClosed);
        Assert.Equal(4400, received.CloseCode);
        Assert.Equal(4400, await closed.Task);
    }

    [Fact]
    public async Task Send_over_the_limit_throws()
    {
        var (a, _) = MemoryDuplex.Create();
        await using var client = new StreamWireConnection(a, Quic, new StreamWireConnectionOptions { MaxFrameBytes = 10 });
        await Assert.ThrowsAsync<ArgumentException>(async () => await client.SendAsync(new byte[11], Ct));
    }

    [Fact]
    public async Task First_frame_is_capped_at_8_KiB_then_the_cap_lifts()
    {
        var (a, b) = MemoryDuplex.Create();
        await using var server = new StreamWireConnection(b, Quic);
        await a.WriteAsync(Framed(new byte[8 * 1024]), Ct);
        Assert.Equal(8 * 1024, (await server.ReceiveAsync(Ct)).Frame.Length);
        await a.WriteAsync(Framed(new byte[20 * 1024]), Ct);
        Assert.Equal(20 * 1024, (await server.ReceiveAsync(Ct)).Frame.Length);

        var (c, d) = MemoryDuplex.Create();
        await using var second = new StreamWireConnection(d, Quic);
        await c.WriteAsync(Framed(new byte[8 * 1024 + 1]), Ct);
        Assert.Equal(4400, (await second.ReceiveAsync(Ct)).CloseCode);
    }

    [Fact]
    public async Task Silence_before_the_first_frame_closes_with_4408()
    {
        var (_, b) = MemoryDuplex.Create();
        await using var server = new StreamWireConnection(b, Quic, new StreamWireConnectionOptions { PreHelloTimeout = TimeSpan.FromMilliseconds(100) });
        var received = await server.ReceiveAsync(Ct);
        Assert.Equal(StreamWireConnection.HelloTimeout, received.CloseCode);
    }

    [Fact]
    public async Task Peer_end_of_stream_is_a_plain_close()
    {
        var (a, b) = MemoryDuplex.Create();
        await using var server = new StreamWireConnection(b, Quic);
        await a.DisposeAsync();
        var received = await server.ReceiveAsync(Ct);
        Assert.True(received.IsClosed);
        Assert.Equal(0, received.CloseCode);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_a_hello_timeout()
    {
        var (_, b) = MemoryDuplex.Create();
        await using var server = new StreamWireConnection(b, Quic);
        using var cts = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await server.ReceiveAsync(cts.Token));
    }
}
