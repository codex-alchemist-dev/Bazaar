namespace Bazaar.Conduit.PeerToPeer;

/// <summary>
/// The path that carries signaling messages between two peers, for example the relay or a paired device. The
/// implementation is injected, so the state machine never depends on a transport.
/// </summary>
public interface IRendezvousChannel
{
    /// <summary>Sends one message to the peer. Throws when the channel is closed.</summary>
    ValueTask SendAsync(SignalMessage message, CancellationToken cancellationToken);

    /// <summary>Waits for the next message from the peer, or returns null once the channel is closed.</summary>
    ValueTask<SignalMessage?> ReceiveAsync(CancellationToken cancellationToken);
}
