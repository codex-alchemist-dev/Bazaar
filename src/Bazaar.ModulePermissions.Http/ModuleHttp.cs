using System.Net;
using System.Net.Sockets;

namespace Bazaar.ModulePermissions.Http;

/// <summary>Resolves a host name to addresses. A seam so tests can supply the DNS answer.</summary>
public delegate ValueTask<IPAddress[]> ModuleHostResolver(string host, CancellationToken ct);

/// <summary>
/// Builds the only HTTP stack a hosted module is given (doc 48 section 1: "cannot reach a host it did not name"). Every
/// request passes <see cref="ModuleHttpGuard"/>, redirects are not followed (a redirect is a new request the module must
/// make, and so a new check), no proxy or ambient credential is used, and each connection is opened here to an address
/// that was resolved and vetted here, so the name that was checked is the address that is dialled.
/// </summary>
public static class ModuleHttp
{
    /// <summary>Creates the guarded handler for one module.</summary>
    public static HttpMessageHandler CreateHandler(IModulePermissionGate gate, string moduleId, ModuleHostResolver? resolver = null)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);

        var resolve = resolver ?? (static async (host, ct) => await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false));
        var transport = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            Credentials = null,
            PreAuthenticate = false,
            ConnectCallback = (context, ct) => ConnectAsync(gate, moduleId, resolve, context.DnsEndPoint, ct),
        };
        return new ModuleHttpGuard(gate, moduleId) { InnerHandler = transport };
    }

    /// <summary>
    /// Picks the addresses a connection to <paramref name="host"/> may use. The host is checked against the gate again here,
    /// because this is the name that is really dialled. An IP literal is used as granted. A name must resolve to public
    /// addresses only: one private answer refuses the whole name, so a mixed answer cannot be used to probe.
    /// </summary>
    public static async ValueTask<IPAddress[]> VetAsync(
        IModulePermissionGate gate, string moduleId, ModuleHostResolver resolver, string host, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(host);

        gate.Demand(moduleId, new ModuleAccess(ModulePermissionClasses.Network, host));
        if (IPAddress.TryParse(host, out var literal))
        {
            return [literal];
        }

        var addresses = await resolver(host, ct).ConfigureAwait(false);
        if (addresses is not { Length: > 0 } || !addresses.All(ModuleAddressRules.IsPublic))
        {
            throw new ModuleNetworkRefusedException(host, "The host resolves to no address or to an address that is not public.");
        }

        return addresses;
    }

    private static async ValueTask<Stream> ConnectAsync(
        IModulePermissionGate gate, string moduleId, ModuleHostResolver resolver, DnsEndPoint endpoint, CancellationToken ct)
    {
        var addresses = await VetAsync(gate, moduleId, resolver, endpoint.Host, ct).ConfigureAwait(false);
        Exception? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), ct).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException e)
            {
                socket.Dispose();
                last = e;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw new HttpRequestException($"No address of the host accepted the connection.", last);
    }
}

/// <summary>Thrown when a module's request is refused for a network reason the permission gate does not express.</summary>
public sealed class ModuleNetworkRefusedException(string host, string reason) : UnauthorizedAccessException(reason)
{
    /// <summary>The host of the refused request, or an empty string when the request named none.</summary>
    public string Host { get; } = host;
}
