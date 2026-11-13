using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Bazaar.Conduit.PeerToPeer;

/// <summary>
/// Sends one STUN request to one server and returns the reply bytes, or null when nothing arrives in time. This is the
/// seam that tests replace; the real network path is <see cref="UdpStunTransport"/>.
/// </summary>
public interface IStunTransport
{
    /// <summary>Sends <paramref name="request"/> to <paramref name="server"/> and waits up to <paramref name="timeout"/>.</summary>
    /// <returns>The first datagram from that server, or null on timeout.</returns>
    Task<byte[]?> ExchangeAsync(IPEndPoint server, byte[] request, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>
/// UDP implementation of <see cref="IStunTransport"/>. Follows the RFC 5389 retransmission idea: the request is sent
/// again after 500 ms, 1 s and 2 s without a reply (three retransmissions), and the last attempt waits for whatever
/// remains of the caller's budget. Datagrams from other endpoints are ignored.
/// </summary>
public sealed class UdpStunTransport : IStunTransport
{
    private static readonly TimeSpan[] _retransmitWaits =
    [
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
    ];

    /// <inheritdoc />
    public async Task<byte[]?> ExchangeAsync(IPEndPoint server, byte[] request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            using var udp = new UdpClient(server.AddressFamily);
            var clock = Stopwatch.StartNew();
            for (var attempt = 0; attempt <= _retransmitWaits.Length; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var remaining = timeout - clock.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    return null;
                }

                await udp.SendAsync(request, request.Length, server).ConfigureAwait(false);

                var wait = attempt < _retransmitWaits.Length ? TimeSpan.FromTicks(Math.Min(_retransmitWaits[attempt].Ticks, remaining.Ticks)) : remaining;
                var reply = await WaitForReplyAsync(udp, server, wait, cancellationToken).ConfigureAwait(false);
                if (reply is not null)
                {
                    return reply;
                }
            }

            return null;
        }
        catch (SocketException)
        {
            // A send or receive failure is reported as "no reply" so the caller can try the next server.
            return null;
        }
    }

    private static async Task<byte[]?> WaitForReplyAsync(UdpClient udp, IPEndPoint server, TimeSpan wait, CancellationToken cancellationToken)
    {
        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        waitCts.CancelAfter(wait);
        try
        {
            while (true)
            {
                var result = await udp.ReceiveAsync(waitCts.Token).ConfigureAwait(false);
                if (IsFrom(result.RemoteEndPoint, server))
                {
                    return result.Buffer;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private static bool IsFrom(IPEndPoint from, IPEndPoint server) =>
        from.Port == server.Port && from.Address.MapToIPv6().Equals(server.Address.MapToIPv6());
}
