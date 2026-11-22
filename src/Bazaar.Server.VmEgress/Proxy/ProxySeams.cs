using System.Net;
using System.Net.Sockets;

namespace Bazaar.Server.VmEgress.Proxy;

/// <summary>Resolves a host name. The proxy resolves for the guest (doc 16: the guest has no resolver route) and checks the answer.</summary>
internal interface IHostResolver
{
    /// <summary>All addresses for <paramref name="host"/>. Empty when the name does not resolve.</summary>
    ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken ct);
}

/// <summary>Opens the upstream connection to an address the proxy has already checked and pinned.</summary>
internal interface IUpstreamConnector
{
    /// <summary>Connects to exactly <paramref name="endpoint"/>. No name lookup happens here.</summary>
    ValueTask<Socket> ConnectAsync(IPEndPoint endpoint, CancellationToken ct);
}

internal sealed class SystemHostResolver : IHostResolver
{
    public async ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken ct)
    {
        try
        {
            return await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            return [];
        }
    }
}

internal sealed class SocketUpstreamConnector : IUpstreamConnector
{
    public async ValueTask<Socket> ConnectAsync(IPEndPoint endpoint, CancellationToken ct)
    {
        var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(endpoint, ct).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
