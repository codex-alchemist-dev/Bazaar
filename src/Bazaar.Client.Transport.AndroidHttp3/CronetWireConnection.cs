using System.Globalization;
using Bazaar.Wire.Abstractions;

namespace Bazaar.Client.Transport.AndroidHttp3;

/// <summary>A Cronet bidirectional stream seen as a wire connection: gRPC messages in, codec frames out.</summary>
internal sealed class CronetWireConnection : IWireConnection
{
    private readonly ICronetBidirectionalStream _stream;
    private readonly IWireConnectionObserver _observer;
    private readonly int _maxFrameBytes;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly byte[] _prefix = new byte[GrpcMessageFraming.PrefixBytes];
    private CronetNetworkEvent? _lastNetworkEvent;
    private WireReceive? _closed;

    public CronetWireConnection(ICronetBidirectionalStream stream, WireProfile profile, int maxFrameBytes, IWireConnectionObserver observer)
    {
        _stream = stream;
        Profile = profile;
        _maxFrameBytes = maxFrameBytes;
        _observer = observer;
        _stream.NetworkEvent += OnNetworkEvent;
    }

    public WireProfile Profile { get; }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
    {
        if (frame.Length > _maxFrameBytes) throw new ArgumentException("Frame exceeds the maximum size.", nameof(frame));
        var message = GrpcMessageFraming.Encode(frame.Span);
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            _observer.OnReconnectRequired(ReconnectReason.StreamFailed, _lastNetworkEvent);
            throw;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async ValueTask<WireReceive> ReceiveAsync(CancellationToken cancellationToken)
    {
        if (_closed is { } already) return already;
        try
        {
            var result = await ReadOneAsync(cancellationToken).ConfigureAwait(false);
            if (result.IsClosed) _closed = result;
            return result;
        }
        catch (IOException)
        {
            _observer.OnReconnectRequired(ReconnectReason.StreamFailed, _lastNetworkEvent);
            return (_closed = WireReceive.Closed()).Value;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stream.NetworkEvent -= OnNetworkEvent;
        try
        {
            await _stream.CompleteWritesAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
        }

        await _stream.DisposeAsync().ConfigureAwait(false);
        _sendLock.Dispose();
    }

    private async ValueTask<WireReceive> ReadOneAsync(CancellationToken cancellationToken)
    {
        var got = await ReadExactAsync(_prefix, cancellationToken).ConfigureAwait(false);
        if (got == 0) return EndOfStream();
        if (got < _prefix.Length) return Cut();
        if (!GrpcMessageFraming.TryReadPrefix(_prefix, _maxFrameBytes + GrpcMessageFraming.FieldHeaderAllowance, out var length))
        {
            return WireReceive.Closed(4400);
        }

        var body = new byte[length];
        if (await ReadExactAsync(body, cancellationToken).ConfigureAwait(false) < length) return Cut();
        return GrpcMessageFraming.TryDecode(body, _maxFrameBytes, out var frame) ? WireReceive.Data(frame) : WireReceive.Closed(4400);
    }

    private async ValueTask<int> ReadExactAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await _stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (n == 0) break;
            total += n;
        }

        return total;
    }

    private WireReceive Cut()
    {
        _observer.OnReconnectRequired(ReconnectReason.StreamCut, _lastNetworkEvent);
        return WireReceive.Closed();
    }

    private WireReceive EndOfStream()
    {
        if (!_stream.EndedCleanly) return Cut();
        var trailers = _stream.Trailers;
        if (trailers.TryGetValue(GrpcMetadata.CloseCode, out var text)
            && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var code))
        {
            return WireReceive.Closed(code);
        }

        return WireReceive.Closed(GrpcMetadata.CloseCodeOfStatus(trailers));
    }

    private void OnNetworkEvent(CronetNetworkEvent networkEvent)
    {
        _lastNetworkEvent = networkEvent;
        _observer.OnNetworkEvent(networkEvent);
    }
}
