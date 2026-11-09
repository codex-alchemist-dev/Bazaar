using System.Net;
using System.Net.Sockets;
using Bazaar.Server.Transports.LanMdns;

namespace Bazaar.Server.Transports.Tests.LanMdns;

/// <summary>Socket fake: records what is sent and lets a test deliver a datagram. Never binds a real socket.</summary>
internal sealed class FakeMulticastResponder(bool failStart = false) : IMulticastResponder
{
    private Func<MulticastDatagram, CancellationToken, ValueTask>? _onDatagram;

    public List<byte[]> Sent { get; } = [];

    public bool Started => _onDatagram is not null;

    public bool Disposed { get; private set; }

    public Task StartAsync(Func<MulticastDatagram, CancellationToken, ValueTask> onDatagram, CancellationToken cancellationToken)
    {
        if (failStart)
        {
            throw new SocketException((int)SocketError.AddressAlreadyInUse);
        }

        _onDatagram = onDatagram;
        return Task.CompletedTask;
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
    {
        Sent.Add(packet.ToArray());
        return ValueTask.CompletedTask;
    }

    /// <summary>Delivers a datagram as if a peer had sent it to the group.</summary>
    public ValueTask DeliverAsync(byte[] payload) =>
        _onDatagram!(new MulticastDatagram(new IPEndPoint(IPAddress.Parse("192.168.1.50"), 5353), payload), CancellationToken.None);

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Address fake with a fixed list of LAN addresses.</summary>
internal sealed class FakeLanAddressSource(params string[] addresses) : ILanAddressSource
{
    public IReadOnlyList<IPAddress> GetLanAddresses() => addresses.Select(IPAddress.Parse).ToList();
}
