using Bazaar.Client.Transport.Grpc.Generated;
using Bazaar.Wire.Abstractions;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;

namespace Bazaar.Client.Transport.Grpc;

/// <summary>A connected <c>grpc</c> stream seen as a wire connection.</summary>
internal sealed class GrpcClientConnection : IWireConnection
{
    private readonly GrpcChannel _channel;
    private readonly AsyncDuplexStreamingCall<Frame, Frame> _call;
    private readonly int _maxFrameBytes;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public GrpcClientConnection(GrpcChannel channel, AsyncDuplexStreamingCall<Frame, Frame> call, WireProfile profile, int maxFrameBytes)
    {
        _channel = channel;
        _call = call;
        Profile = profile;
        _maxFrameBytes = maxFrameBytes;
    }

    public WireProfile Profile { get; }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
    {
        if (frame.Length > _maxFrameBytes) throw new ArgumentException("Frame exceeds the maximum size.", nameof(frame));
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _call.RequestStream.WriteAsync(new Frame { Data = ByteString.CopyFrom(frame.Span) }, cancellationToken).ConfigureAwait(false);
        }
        catch (RpcException ex)
        {
            throw new IOException("gRPC send failed: " + ex.StatusCode.ToString(), ex);
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
            if (await _call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false))
            {
                var data = _call.ResponseStream.Current.Data;
                if (data.Length > _maxFrameBytes) return WireReceive.Closed(4400);
                return WireReceive.Data(data.Memory);
            }

            return WireReceive.Closed(CloseCodeOf(_call.GetTrailers()));
        }
        catch (RpcException ex) when (ex.StatusCode != StatusCode.Cancelled || !cancellationToken.IsCancellationRequested)
        {
            var code = CloseCodeOf(ex.Trailers);
            if (code == 0) code = ex.StatusCode switch { StatusCode.ResourceExhausted => 4400, StatusCode.Unauthenticated => 4401, _ => 0 };
            return WireReceive.Closed(code);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _call.RequestStream.CompleteAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or RpcException or IOException)
        {
        }

        _call.Dispose();
        _channel.Dispose();
        _sendLock.Dispose();
    }

    private static int CloseCodeOf(Metadata trailers)
    {
        var value = trailers.GetValue(GrpcMetadata.CloseCode);
        return value is not null && int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var code) ? code : 0;
    }
}
