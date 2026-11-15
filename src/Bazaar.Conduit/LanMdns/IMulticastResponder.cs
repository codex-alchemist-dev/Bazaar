using System.Net;

namespace Bazaar.Conduit.LanMdns;

/// <summary>One datagram received on the mDNS group.</summary>
/// <param name="Source">Sender address and port.</param>
/// <param name="Payload">Raw DNS packet.</param>
public sealed record MulticastDatagram(IPEndPoint Source, byte[] Payload);

/// <summary>
/// Socket seam for mDNS: joins the group, receives datagrams and sends to the group. The real implementation is
/// <see cref="UdpMulticastResponder"/>. Tests use a fake, so no test binds port 5353.
/// </summary>
public interface IMulticastResponder : IAsyncDisposable
{
    /// <summary>
    /// Binds the socket, joins the group and delivers each received datagram to <paramref name="onDatagram"/>.
    /// Throws when the socket cannot be bound or joined.
    /// </summary>
    Task StartAsync(Func<MulticastDatagram, CancellationToken, ValueTask> onDatagram, CancellationToken cancellationToken);

    /// <summary>Sends one packet to the mDNS group.</summary>
    ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken);
}
