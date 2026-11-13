using System.Threading.Channels;
using Bazaar.Conduit.PeerToPeer;

namespace Bazaar.Conduit.Tests.PeerToPeer;

/// <summary>
/// One end of an in-memory rendezvous channel. Each end sends into one unbounded queue and receives from the other.
/// <see cref="Close"/> completes this end's outgoing queue, so the peer's receive returns null after the queue drains.
/// </summary>
internal sealed class InMemoryRendezvousChannel : IRendezvousChannel
{
    private readonly ChannelWriter<SignalMessage> _outgoing;
    private readonly ChannelReader<SignalMessage> _incoming;

    private InMemoryRendezvousChannel(ChannelWriter<SignalMessage> outgoing, ChannelReader<SignalMessage> incoming)
    {
        _outgoing = outgoing;
        _incoming = incoming;
    }

    /// <summary>Creates two connected ends. A message sent on one is received on the other.</summary>
    public static (InMemoryRendezvousChannel First, InMemoryRendezvousChannel Second) CreatePair()
    {
        var firstToSecond = Channel.CreateUnbounded<SignalMessage>();
        var secondToFirst = Channel.CreateUnbounded<SignalMessage>();

        var first = new InMemoryRendezvousChannel(firstToSecond.Writer, secondToFirst.Reader);
        var second = new InMemoryRendezvousChannel(secondToFirst.Writer, firstToSecond.Reader);
        return (first, second);
    }

    /// <summary>Messages this end has sent, in order. Used to check what a session put on the wire.</summary>
    public List<SignalMessage> Sent { get; } = [];

    public ValueTask SendAsync(SignalMessage message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Sent.Add(message);
        return _outgoing.WriteAsync(message, cancellationToken);
    }

    public async ValueTask<SignalMessage?> ReceiveAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _incoming.ReadAsync(cancellationToken);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    /// <summary>Closes this end's outgoing side. The peer's pending or future receives return null.</summary>
    public void Close() => _outgoing.TryComplete();
}
