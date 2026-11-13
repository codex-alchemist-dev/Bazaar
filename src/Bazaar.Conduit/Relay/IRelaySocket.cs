namespace Bazaar.Conduit.Relay;

/// <summary>
/// One outbound connection to a relay. This is the seam the relay module uses; the real implementation is
/// <see cref="ClientWebSocketRelaySocket"/>, and tests replace it with a fake. The relay forwards opaque bytes.
/// </summary>
public interface IRelaySocket : IDisposable
{
    /// <summary>Opens the connection to <paramref name="uri"/>.</summary>
    Task ConnectAsync(Uri uri, CancellationToken cancellationToken);

    /// <summary>Sends one text message.</summary>
    Task SendAsync(byte[] payload, CancellationToken cancellationToken);

    /// <summary>Waits for one complete message, or returns null when the relay closed the connection.</summary>
    Task<byte[]?> ReceiveAsync(CancellationToken cancellationToken);

    /// <summary>Sends a normal close when the connection is still open.</summary>
    Task CloseAsync(CancellationToken cancellationToken);
}
