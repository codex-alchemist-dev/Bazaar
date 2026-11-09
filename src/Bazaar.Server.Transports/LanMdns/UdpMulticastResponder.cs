using System.Net;
using System.Net.Sockets;

namespace Bazaar.Server.Transports.LanMdns;

/// <summary>
/// Real mDNS socket: IPv4 UDP on 224.0.0.251:5353, joined on each given interface address. Sends go out on every
/// interface. Only the LanMdns module creates it.
/// </summary>
public sealed class UdpMulticastResponder(IReadOnlyList<IPAddress> interfaceAddresses) : IMulticastResponder
{
    /// <summary>mDNS IPv4 group address.</summary>
    public static readonly IPAddress Group = IPAddress.Parse("224.0.0.251");

    /// <summary>mDNS port.</summary>
    public const int Port = 5353;

    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private UdpClient? _client;
    private CancellationTokenSource? _stop;
    private Task? _receiveLoop;

    /// <inheritdoc />
    public Task StartAsync(Func<MulticastDatagram, CancellationToken, ValueTask> onDatagram, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onDatagram);
        cancellationToken.ThrowIfCancellationRequested();
        if (_client is not null)
        {
            throw new InvalidOperationException("The socket is already started.");
        }

        var client = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            client.Client.Bind(new IPEndPoint(IPAddress.Any, Port));

            if (interfaceAddresses.Count == 0)
            {
                client.JoinMulticastGroup(Group);
            }
            else
            {
                foreach (var address in interfaceAddresses)
                {
                    client.JoinMulticastGroup(Group, address);
                }
            }
        }
        catch
        {
            client.Dispose();
            throw;
        }

        _client = client;
        _stop = new CancellationTokenSource();
        _receiveLoop = Task.Run(() => ReceiveAsync(client, onDatagram, _stop.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
    {
        var client = _client ?? throw new InvalidOperationException("The socket is not started.");
        var target = new IPEndPoint(Group, Port);

        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (interfaceAddresses.Count == 0)
            {
                await client.SendAsync(packet, target, cancellationToken).ConfigureAwait(false);
                return;
            }

            foreach (var address in interfaceAddresses)
            {
                client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, address.GetAddressBytes());
                await client.SendAsync(packet, target, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        var stop = _stop;
        var client = _client;
        var loop = _receiveLoop;
        _stop = null;
        _client = null;
        _receiveLoop = null;

        stop?.Cancel();
        client?.Dispose();
        if (loop is not null)
        {
            await loop.ConfigureAwait(false);
        }

        stop?.Dispose();
        _sendGate.Dispose();
    }

    private static async Task ReceiveAsync(UdpClient client, Func<MulticastDatagram, CancellationToken, ValueTask> onDatagram, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await client.ReceiveAsync(stop).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            try
            {
                await onDatagram(new MulticastDatagram(received.RemoteEndPoint, received.Buffer), stop).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex) when (ex is SocketException or InvalidOperationException)
            {
                // A failed reply must not stop the listener; the next query gets its own attempt.
            }
        }
    }
}
