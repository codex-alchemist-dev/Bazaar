using System.Net;
using System.Net.Sockets;

namespace Bazaar.ModulePermissions.Http;

/// <summary>
/// Which addresses a module's connection may land on when it asked for a host by name. A name the owner approved must not
/// become a way into the server's own network: a DNS answer that points at loopback, a private range, link-local (cloud
/// metadata lives there), carrier NAT, multicast or an unspecified address is refused. A module that needs such an address
/// declares the IP literal itself (<c>network:10.0.0.5</c>), which the owner then sees on the approval card.
/// </summary>
public static class ModuleAddressRules
{
    /// <summary>True when <paramref name="address"/> is a public unicast address.</summary>
    public static bool IsPublic(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.Broadcast))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] switch
            {
                0 or 10 or 127 => false,
                100 when b[1] is >= 64 and <= 127 => false, // carrier NAT 100.64/10
                169 when b[1] == 254 => false, // link-local, cloud metadata
                172 when b[1] is >= 16 and <= 31 => false,
                192 when b[1] == 168 => false,
                192 when b[1] == 0 && b[2] == 0 => false, // protocol assignments 192.0.0/24
                198 when b[1] is 18 or 19 => false, // benchmarking 198.18/15
                >= 224 => false, // multicast and reserved
                _ => true,
            };
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return false;
        }

        if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || address.IsIPv6UniqueLocal)
        {
            return false;
        }

        var v6 = address.GetAddressBytes();

        // NAT64 (64:ff9b::/96) and 6to4 (2002::/16) carry an IPv4 address: judge that address.
        if (v6[0] == 0x00 && v6[1] == 0x64 && v6[2] == 0xff && v6[3] == 0x9b && v6.AsSpan(4, 8).IndexOfAnyExcept((byte)0) < 0)
        {
            return IsPublic(new IPAddress(v6.AsSpan(12, 4)));
        }

        if (v6[0] == 0x20 && v6[1] == 0x02)
        {
            return IsPublic(new IPAddress(v6.AsSpan(2, 4)));
        }

        // Only global unicast (2000::/3) is public; everything else is reserved or special.
        return (v6[0] & 0xE0) == 0x20;
    }
}
