using System.Buffers;
using System.Net.WebSockets;
using Bazaar.Contracts.Wire;

namespace Bazaar.Client.Core.Wire;

/// <summary>
/// Opens <see cref="ClientWebSocket"/> connections to the server (docs/design/02-wire-protocol-and-sync.md).
/// Platform-neutral: no UI types. Each connect creates a new socket; <c>configure</c> runs before the connect so
/// callers can set the <c>Authorization</c> header. Connect failures propagate as exceptions for the client to retry.
/// </summary>
public sealed class WebSocketWireTransportFactory : IWireTransportFactory
{
    private readonly Uri _endpoint;
    private readonly Action<ClientWebSocketOptions>? _configure;
    private readonly int _maxFrameBytes;
    private readonly WebSocketMessageType _messageType;

    /// <summary>Creates a factory for one server endpoint.</summary>
    /// <param name="endpoint">The socket URL, such as <c>wss://host/ws</c>.</param>
    /// <param name="configure">Optional setup of the socket options (headers, proxy) before connecting.</param>
    /// <param name="maxFrameBytes">The largest message accepted; a larger one closes the socket with 4400.</param>
    /// <param name="binary">Sends messages as binary frames instead of text (the JSON codec is text).</param>
    public WebSocketWireTransportFactory(Uri endpoint, Action<ClientWebSocketOptions>? configure = null, int maxFrameBytes = 256 * 1024, bool binary = false)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFrameBytes);
        _endpoint = endpoint;
        _configure = configure;
        _maxFrameBytes = maxFrameBytes;
        _messageType = binary ? WebSocketMessageType.Binary : WebSocketMessageType.Text;
    }

    /// <inheritdoc />
    public async ValueTask<IWireConnection> ConnectAsync(CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        try
        {
            _configure?.Invoke(socket.Options);
            await socket.ConnectAsync(_endpoint, cancellationToken).ConfigureAwait(false);
            return new WebSocketWireConnection(socket, _maxFrameBytes, _messageType);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

/// <summary>
/// One open socket. Sends are serialized so concurrent callers never interleave a message. Receives are expected
/// from one loop at a time (<see cref="WireClient"/> runs one).
/// </summary>
internal sealed class WebSocketWireConnection(ClientWebSocket socket, int maxFrameBytes, WebSocketMessageType sendType) : IWireConnection
{
    private const int _chunkBytes = 16 * 1024;

    /// <summary>RFC 6455 1005: the peer sent a close frame with no status. Reported as 0 (no code).</summary>
    private const int _noStatusReceived = 1005;
    private static readonly TimeSpan _closeTimeout = TimeSpan.FromSeconds(2);
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    /// <inheritdoc />
    public async ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
    {
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(frame, sendType, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<WireReceive> ReceiveAsync(CancellationToken cancellationToken)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseSent))
        {
            return WireReceive.Closed(CloseCode());
        }

        var message = new ArrayBufferWriter<byte>();
        byte[] chunk = ArrayPool<byte>.Shared.Rent(_chunkBytes);
        try
        {
            while (true)
            {
                WebSocketReceiveResult result;
                try
                {
                    result = await socket.ReceiveAsync(chunk, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException)
                {
                    return WireReceive.Closed(0); // dropped or aborted without a close frame
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await EchoCloseAsync().ConfigureAwait(false);
                    return WireReceive.Closed(CloseCode());
                }

                if (message.WrittenCount + result.Count > maxFrameBytes)
                {
                    await SendCloseAsync((WebSocketCloseStatus)WireCloseCodes.BadFrame, "frame too large").ConfigureAwait(false);
                    return WireReceive.Closed(WireCloseCodes.BadFrame);
                }

                message.Write(chunk.AsSpan(0, result.Count));
                if (result.EndOfMessage) return WireReceive.Data(message.WrittenMemory);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
    }

    /// <summary>Closes politely when still open, then disposes. Errors are swallowed: the socket is dead either way.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                using var timeout = new CancellationTokenSource(_closeTimeout);
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // The peer is gone or slow; disposing below is the cleanup that matters.
        }
        finally
        {
            socket.Dispose();
            _sendGate.Dispose();
        }
    }

    /// <summary>The close code the server sent, or 0 when none arrived (dropped, or the code was absent).</summary>
    private int CloseCode()
    {
        int code = socket.CloseStatus is { } status ? (int)status : 0;
        return code == _noStatusReceived ? 0 : code;
    }

    /// <summary>Completes a close the server started by sending the matching close frame.</summary>
    private async Task EchoCloseAsync()
    {
        if (socket.State != WebSocketState.CloseReceived) return;
        var status = socket.CloseStatus is { } s && (int)s != _noStatusReceived ? s : WebSocketCloseStatus.NormalClosure;
        await SendCloseAsync(status, socket.CloseStatusDescription).ConfigureAwait(false);
    }

    /// <summary>Sends a close frame with a short timeout, ignoring failures.</summary>
    private async Task SendCloseAsync(WebSocketCloseStatus status, string? description)
    {
        try
        {
            using var timeout = new CancellationTokenSource(_closeTimeout);
            await _sendGate.WaitAsync(timeout.Token).ConfigureAwait(false);
            try
            {
                await socket.CloseOutputAsync(status, description, timeout.Token).ConfigureAwait(false);
            }
            finally
            {
                _sendGate.Release();
            }
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The peer is gone; the caller reports the close either way.
        }
    }
}
