using System.Threading.Channels;
using Bazaar.Server.Transport.Grpc.Generated;
using Bazaar.Wire.Abstractions;
using Google.Protobuf;
using Grpc.Core;

namespace Bazaar.Server.Transport.Grpc;

/// <summary>One gRPC-Web session: a <c>Subscribe</c> stream out, <c>Send</c> calls in, seen as one wire connection.</summary>
internal sealed class GrpcWebSession : ICloseableWireConnection
{
    private readonly IServerStreamWriter<Frame> _writer;
    private readonly int _maxFrameBytes;
    private readonly Channel<byte[]> _inbound;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private int _closeCode = -1;

    public GrpcWebSession(string credential, WireProfile profile, IServerStreamWriter<Frame> writer, int maxFrameBytes, int queueDepth)
    {
        Credential = credential;
        Profile = profile;
        _writer = writer;
        _maxFrameBytes = maxFrameBytes;
        _inbound = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(queueDepth)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    public string Credential { get; }

    public WireProfile Profile { get; }

    public int? CloseCode => Volatile.Read(ref _closeCode) is var c and >= 0 ? c : null;

    /// <summary>Called by <c>Send</c>: queues one client frame for the connection layer.</summary>
    public async ValueTask EnqueueAsync(ByteString data, CancellationToken cancellationToken)
    {
        if (data.Length > _maxFrameBytes) throw new RpcException(new Status(StatusCode.ResourceExhausted, "Frame too large."), GrpcServerConnection.TooLargeTrailers());
        try
        {
            await _inbound.Writer.WriteAsync(data.ToByteArray(), cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "Session closed."), CloseTrailers());
        }
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
    {
        if (frame.Length > _maxFrameBytes) throw new ArgumentException("Frame exceeds the maximum size.", nameof(frame));
        ObjectDisposedException.ThrowIf(CloseCode is not null, this);
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _writer.WriteAsync(new Frame { Data = ByteString.CopyFrom(frame.Span) }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async ValueTask<WireReceive> ReceiveAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (await _inbound.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false) && _inbound.Reader.TryRead(out var frame))
            {
                return WireReceive.Data(frame);
            }
        }
        catch (ChannelClosedException)
        {
        }

        return WireReceive.Closed(CloseCode ?? 0);
    }

    public ValueTask CloseAsync(int code, CancellationToken cancellationToken)
    {
        Interlocked.CompareExchange(ref _closeCode, code, -1);
        _inbound.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    /// <summary>The peer is gone (the <c>Subscribe</c> call was cancelled): ends the connection with no close code.</summary>
    public void Abort() => _inbound.Writer.TryComplete();

    public ValueTask DisposeAsync()
    {
        _inbound.Writer.TryComplete();
        _sendLock.Dispose();
        return ValueTask.CompletedTask;
    }

    internal Metadata CloseTrailers() => CloseCode is { } c ? new Metadata { { GrpcMetadata.CloseCode, c.ToString(System.Globalization.CultureInfo.InvariantCulture) } } : [];
}
