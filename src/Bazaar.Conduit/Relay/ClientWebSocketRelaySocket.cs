using System.Net.WebSockets;

namespace Bazaar.Conduit.Relay;

/// <summary>
/// <see cref="IRelaySocket"/> over <see cref="ClientWebSocket"/>. Outbound only: the server opens no inbound port
/// for the relay.
/// </summary>
public sealed class ClientWebSocketRelaySocket : IRelaySocket
{
    private const int _bufferSize = 8 * 1024;

    private readonly ClientWebSocket _socket = new();

    /// <summary>Creates an unconnected socket with a 30 second keep-alive.</summary>
    public ClientWebSocketRelaySocket()
    {
        _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
    }

    /// <inheritdoc />
    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken) =>
        _socket.ConnectAsync(uri, cancellationToken);

    /// <inheritdoc />
    public Task SendAsync(byte[] payload, CancellationToken cancellationToken) =>
        _socket.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, endOfMessage: true, cancellationToken);

    /// <inheritdoc />
    public async Task<byte[]?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[_bufferSize];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return message.ToArray();
            }
        }
    }

    /// <inheritdoc />
    public async Task CloseAsync(CancellationToken cancellationToken)
    {
        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _socket.Dispose();
}
