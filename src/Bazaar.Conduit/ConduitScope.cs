namespace Bazaar.Conduit;

/// <summary>
/// How far a transport path reaches. Doc 39 section 1. Only <see cref="Loopback"/> may carry plain <c>ws://</c>.
/// </summary>
public enum ConduitScope
{
    /// <summary>127.0.0.1 or ::1 on the host itself.</summary>
    Loopback,

    /// <summary>The home or office LAN (mDNS, direct address).</summary>
    Lan,

    /// <summary>An overlay network such as Tailscale, Headscale or WireGuard.</summary>
    Overlay,

    /// <summary>A public address or hostname reached directly or through a port mapping.</summary>
    Internet,

    /// <summary>An operator-run relay that forwards opaque bytes.</summary>
    Relay,

    /// <summary>A direct peer path found by candidate exchange (STUN and ICE).</summary>
    PeerToPeer,

    /// <summary>A Tor onion service.</summary>
    Onion,
}
