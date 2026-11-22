using Bazaar.Client.Transport.AndroidHttp3;
using Bazaar.Client.Transport.AndroidHttp3.Android;
using Bazaar.Wire.Abstractions;

namespace Bazaar.Client.Transport.AndroidHttp3.Android.Tests;

public sealed class JavaCronetStreamTests
{
    private static readonly CronetStreamRequest _request = new()
    {
        Url = new Uri("https://example.test/bazaar.Wire/Connect"),
        Headers = [],
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(FakeBridge Bridge, ICronetBidirectionalStream Stream)> OpenAsync()
    {
        var bridge = new FakeBridge();
        var stream = await new JavaCronetEngine(bridge).OpenStreamAsync(_request, Ct);
        return (bridge, stream);
    }

    [Fact]
    public void AvailabilityComesFromTheBridge()
    {
        var bridge = new FakeBridge { IsAvailable = false };
        Assert.False(new JavaCronetEngine(bridge).IsAvailable);
    }

    [Fact]
    public async Task OpenWaitsForStreamReady()
    {
        var bridge = new FakeBridge { AutoReady = false };
        var open = new JavaCronetEngine(bridge).OpenStreamAsync(_request, Ct).AsTask();
        Assert.False(open.IsCompleted);
        bridge.Stream!.Callback.OnStreamReady();
        await using var stream = await open;
        Assert.NotNull(stream);
    }

    [Fact]
    public async Task OpenFailureSurfacesAsIoAndCleansUp()
    {
        var bridge = new FakeBridge { AutoReady = false };
        var open = new JavaCronetEngine(bridge).OpenStreamAsync(_request, Ct).AsTask();
        bridge.Stream!.Callback.OnFailed(new InvalidOperationException("quic"));
        await Assert.ThrowsAsync<IOException>(() => open);
        Assert.Equal(0, bridge.NetworkSubscribers);
    }

    [Fact]
    public async Task ResponseHeadCarriesNegotiatedProtocol()
    {
        var (bridge, stream) = await OpenAsync();
        await using var _ = stream;
        var head = stream.ReadResponseHeadAsync(Ct).AsTask();
        bridge.Stream!.Callback.OnResponseHeaders(200, "h3", new Dictionary<string, string> { ["content-type"] = "application/grpc" });
        var result = await head;
        Assert.Equal(200, result.HttpStatus);
        Assert.Equal("h3", result.NegotiatedProtocol);
    }

    [Fact]
    public async Task ReadIsPullBasedAndNeverReadsAhead()
    {
        var (bridge, stream) = await OpenAsync();
        await using var _ = stream;
        Assert.Empty(bridge.Stream!.ReadRequests);

        var buffer = new byte[4];
        var read = stream.ReadAsync(buffer, Ct).AsTask();
        Assert.Equal([4], bridge.Stream.ReadRequests);
        bridge.Stream.Callback.OnReadCompleted(new byte[] { 1, 2, 3 }, endOfStream: false);
        Assert.Equal(3, await read);
        Assert.Equal([4], bridge.Stream.ReadRequests);
    }

    [Fact]
    public async Task ReadRequestIsCappedToTheChunkSize()
    {
        var (bridge, stream) = await OpenAsync();
        await using var _ = stream;
        var read = stream.ReadAsync(new byte[1 << 20], Ct).AsTask();
        Assert.Equal([16 * 1024], bridge.Stream!.ReadRequests);
        bridge.Stream.Callback.OnReadCompleted(new byte[] { 9 }, false);
        await read;
    }

    [Fact]
    public async Task OversizedChunkIsServedInPiecesWithoutAnotherNetworkRead()
    {
        var (bridge, stream) = await OpenAsync();
        await using var _ = stream;
        var buffer = new byte[2];
        var first = stream.ReadAsync(buffer, Ct).AsTask();
        bridge.Stream!.Callback.OnReadCompleted(new byte[] { 1, 2, 3, 4, 5 }, false);
        Assert.Equal(2, await first);
        Assert.Equal([1, 2], buffer);
        Assert.Equal(2, await stream.ReadAsync(buffer, Ct));
        Assert.Equal([3, 4], buffer);
        Assert.Equal(1, await stream.ReadAsync(buffer, Ct));
        Assert.Equal(5, buffer[0]);
        Assert.Single(bridge.Stream.ReadRequests);
    }

    [Fact]
    public async Task SecondConcurrentReadIsRejected()
    {
        var (_, stream) = await OpenAsync();
        await using var _ = stream;
        var first = stream.ReadAsync(new byte[4], Ct).AsTask();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await stream.ReadAsync(new byte[4], Ct));
        Assert.False(first.IsCompleted);
    }

    [Fact]
    public async Task ZeroIsReturnedOnlyOnceTrailersAreFinal()
    {
        var (bridge, stream) = await OpenAsync();
        await using var _ = stream;
        var read = stream.ReadAsync(new byte[4], Ct).AsTask();
        bridge.Stream!.Callback.OnReadCompleted(ReadOnlyMemory<byte>.Empty, endOfStream: true);
        await Task.Delay(50, Ct);
        Assert.False(read.IsCompleted);

        bridge.Stream.Callback.OnTrailers(new Dictionary<string, string> { ["grpc-status"] = "0" });
        bridge.Stream.Callback.OnSucceeded();
        Assert.Equal(0, await read);
        Assert.True(stream.EndedCleanly);
        Assert.Equal("0", stream.Trailers["grpc-status"]);
    }

    [Fact]
    public async Task CancelledStreamEndsWithoutTrailers()
    {
        var (bridge, stream) = await OpenAsync();
        await using var _ = stream;
        var read = stream.ReadAsync(new byte[4], Ct).AsTask();
        bridge.Stream!.Callback.OnCanceled();
        await Assert.ThrowsAsync<IOException>(() => read);
        Assert.False(stream.EndedCleanly);
    }

    [Fact]
    public async Task FailureFaultsPendingReadAndWriteAsIo()
    {
        var (bridge, stream) = await OpenAsync();
        await using var _ = stream;
        bridge.Stream!.CompleteWritesImmediately = false;
        var write = stream.WriteAsync(new byte[] { 1 }, Ct).AsTask();
        var read = stream.ReadAsync(new byte[4], Ct).AsTask();
        bridge.Stream.Callback.OnFailed(new InvalidOperationException("net::ERR_QUIC_PROTOCOL_ERROR"));
        await Assert.ThrowsAsync<IOException>(() => write);
        var ex = await Assert.ThrowsAsync<IOException>(() => read);
        Assert.IsType<InvalidOperationException>(ex.InnerException);
        await Assert.ThrowsAsync<IOException>(async () => await stream.WriteAsync(new byte[] { 2 }, Ct));
    }

    [Fact]
    public async Task WriteCompletesOnCallbackAndSerializesWrites()
    {
        var (bridge, stream) = await OpenAsync();
        await using var _ = stream;
        bridge.Stream!.CompleteWritesImmediately = false;
        var first = stream.WriteAsync(new byte[] { 1, 2 }, Ct).AsTask();
        Assert.False(first.IsCompleted);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await stream.WriteAsync(new byte[] { 3 }, Ct));
        bridge.Stream.Callback.OnWriteCompleted();
        await first;
        Assert.Equal(new byte[] { 1, 2 }, Assert.Single(bridge.Stream.Writes).Data);
    }

    [Fact]
    public async Task CompleteWritesHalfClosesWithAnEmptyEndOfStreamWrite()
    {
        var (bridge, stream) = await OpenAsync();
        await using var _ = stream;
        await stream.CompleteWritesAsync(Ct);
        var (data, end) = Assert.Single(bridge.Stream!.Writes);
        Assert.Empty(data);
        Assert.True(end);
    }

    [Fact]
    public async Task CancellingATokenCancelsTheStream()
    {
        var (bridge, stream) = await OpenAsync();
        await using var _ = stream;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var read = stream.ReadAsync(new byte[4], cts.Token).AsTask();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.Equal(1, bridge.Stream!.Cancels);
    }

    [Fact]
    public async Task NetworkEventsAreRelayedAndUnsubscribedOnDispose()
    {
        var (bridge, stream) = await OpenAsync();
        var seen = new List<WireNetworkEvent>();
        stream.NetworkEvent += seen.Add;
        var handover = new WireNetworkEvent(WireNetworkEventKind.Handover, DateTimeOffset.UnixEpoch, "wifi to cell");
        bridge.Raise(handover);
        bridge.Raise(new WireNetworkEvent(WireNetworkEventKind.Suspended, DateTimeOffset.UnixEpoch));
        bridge.Raise(new WireNetworkEvent(WireNetworkEventKind.Resumed, DateTimeOffset.UnixEpoch));
        Assert.Equal([WireNetworkEventKind.Handover, WireNetworkEventKind.Suspended, WireNetworkEventKind.Resumed], seen.Select(e => e.Kind));
        Assert.Same(handover, seen[0]);

        await stream.DisposeAsync();
        Assert.Equal(0, bridge.NetworkSubscribers);
        bridge.Raise(handover);
        Assert.Equal(3, seen.Count);
    }

    [Fact]
    public async Task DisposeCancelsAnOpenStreamOnceAndIsIdempotent()
    {
        var (bridge, stream) = await OpenAsync();
        await stream.DisposeAsync();
        await stream.DisposeAsync();
        Assert.Equal(1, bridge.Stream!.Cancels);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await stream.ReadAsync(new byte[1], Ct));
    }

    [Fact]
    public async Task DisposeAfterSuccessDoesNotCancel()
    {
        var (bridge, stream) = await OpenAsync();
        bridge.Stream!.Callback.OnSucceeded();
        await stream.DisposeAsync();
        Assert.Equal(0, bridge.Stream.Cancels);
    }
}
