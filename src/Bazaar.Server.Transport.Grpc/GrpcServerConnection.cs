using Bazaar.Server.Transport.Grpc.Generated;
using Bazaar.Wire.Abstractions;
using Google.Protobuf;
using Grpc.Core;

namespace Bazaar.Server.Transport.Grpc;

/// <summary>One accepted native gRPC <c>Connect</c> stream seen as a wire connection.</summary>
internal sealed class GrpcServerConnection : ICloseableWireConnection
{
    private readonly IAsyncStreamReader<Frame> _reader;
    private readonly IServerStreamWriter<Frame> _writer;
    private readonly int _maxFrameBytes;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private int _closeCode = -1;

    public GrpcServerConnection(WireProfile profile, IAsyncStreamReader<Frame> reader, IServerStreamWriter<Frame> writer, int maxFrameBytes)
    {
        Profile = profile;
        _reader = reader;
        _writer = writer;
        _maxFrameBytes = maxFrameBytes;
    }

    public WireProfile Profile { get; }

    /// <summary>The close code the connection layer set, or null.</summary>
    public int? CloseCode => Volatile.Read(ref _closeCode) is var c and >= 0 ? c : null;

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
        if (CloseCode is { } closed) return WireReceive.Closed(closed);
        try
        {
            if (!await _reader.MoveNext(cancellationToken).ConfigureAwait(false)) return WireReceive.Closed(0);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            return WireReceive.Closed(0);
        }

        var data = _reader.Current.Data;
        if (data.Length > _maxFrameBytes) throw new RpcException(new Status(StatusCode.ResourceExhausted, "Frame too large."), TooLargeTrailers());
        return WireReceive.Data(data.Memory);
    }

    public ValueTask CloseAsync(int code, CancellationToken cancellationToken)
    {
        Interlocked.CompareExchange(ref _closeCode, code, -1);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _sendLock.Dispose();
        return ValueTask.CompletedTask;
    }

    internal static Metadata TooLargeTrailers() => new() { { GrpcMetadata.CloseCode, "4400" } };
}
