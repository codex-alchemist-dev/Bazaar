using System.Net;
using System.Net.Sockets;

namespace Bazaar.Conduit;

/// <summary>Builds the offers the overlay modules publish. Overlay offers are always <c>wss</c> with the pinned key.</summary>
internal static class OverlayOffers
{
    /// <summary>True for a TCP port number from 1 to 65535.</summary>
    public static bool IsValidPort(int port) => port is >= 1 and <= 65535;

    /// <summary>Creates the stable <c>wss://&lt;address&gt;:&lt;port&gt;/ws</c> offer for an overlay address.</summary>
    public static ConduitOffer Create(string moduleId, IPAddress address, int port, string serverKeyHash)
    {
        var host = address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
        var uri = new Uri($"{Uri.UriSchemeWss}://{host}:{port}/ws", UriKind.Absolute);
        return new ConduitOffer(moduleId, ConduitScope.Overlay, uri, serverKeyHash, null, false, false);
    }
}
