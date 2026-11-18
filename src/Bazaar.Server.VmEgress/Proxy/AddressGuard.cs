using System.Net;
using System.Net.Sockets;

namespace Bazaar.Server.VmEgress.Proxy;

/// <summary>
/// Decides which resolved addresses the proxy may connect to. It refuses everything that is not ordinary public unicast:
/// loopback, private, link-local (cloud metadata included), carrier-grade NAT, multicast, reserved, documentation and
/// transition ranges that wrap an IPv4 address. This is a security floor and has no setting.
/// </summary>
public static class AddressGuard
{
    /// <summary>True when the proxy must not connect to <paramref name="address"/>.</summary>
    public static bool IsForbidden(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return IsForbiddenV6(address);
        }

        return address.AddressFamily != AddressFamily.InterNetwork || IsForbiddenV4(address.GetAddressBytes());
    }

    private static bool IsForbiddenV4(ReadOnlySpan<byte> b)
    {
        var (a, b1, b2) = (b[0], b[1], b[2]);
        return a == 0                                    // "this network", 0.0.0.0
            || a == 10                                   // private
            || a == 127                                  // loopback
            || (a == 100 && b1 is >= 64 and <= 127)      // carrier-grade NAT, includes 100.100.100.200 metadata
            || (a == 169 && b1 == 254)                   // link-local, includes 169.254.169.254 metadata
            || (a == 172 && b1 is >= 16 and <= 31)       // private
            || (a == 192 && b1 == 0 && b2 == 0)          // IETF protocol assignments
            || (a == 192 && b1 == 0 && b2 == 2)          // documentation
            || (a == 192 && b1 == 168)                   // private
            || (a == 198 && b1 is 18 or 19)              // benchmarking
            || (a == 198 && b1 == 51 && b2 == 100)       // documentation
            || (a == 203 && b1 == 0 && b2 == 113)        // documentation
            || a >= 224                                  // multicast, reserved, broadcast
            || (a == 168 && b1 == 63 && b2 == 129 && b[3] == 16); // Azure wire server, a public-looking host service
    }

    private static bool IsForbiddenV6(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            return IsForbiddenV4(address.MapToIPv4().GetAddressBytes());
        }

        Span<byte> b = stackalloc byte[16];
        address.TryWriteBytes(b, out _);

        if (address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6Loopback))
        {
            return true;
        }

        // Only global unicast 2000::/3 is a candidate. Everything else is unique-local, link-local, site-local, multicast,
        // reserved, or a NAT64 prefix (64:ff9b::/96) that wraps an IPv4 address.
        if ((b[0] & 0xE0) != 0x20)
        {
            return true;
        }

        // Teredo 2001::/32 and documentation 2001:db8::/32 are refused.
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x00)
        {
            return true;
        }

        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8)
        {
            return true;
        }

        // 6to4 (2002::/16) embeds an IPv4 address in bytes 2..5, so it is refused rather than unwrapped.
        return b[0] == 0x20 && b[1] == 0x02;
    }
}
