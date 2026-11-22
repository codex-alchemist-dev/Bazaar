using System.Net;
using Bazaar.Server.VmEgress.Proxy;

namespace Bazaar.Server.VmEgress.Tests;

/// <summary>Range edges for the reserved blocks added after the first review: first, last, one below and one above each.</summary>
public class AddressGuardRangeTests
{
    public static TheoryData<string, bool> Edges =>
        new()
        {
            // 192.88.99.0/24, 6to4 relay anycast
            { "192.88.98.255", false },
            { "192.88.99.0", true },
            { "192.88.99.255", true },
            { "192.88.100.0", false },

            // 2001::/23, IETF protocol assignments (Teredo, benchmarking 2001:2::/48, ORCHID 2001:10::/28 and 2001:20::/28)
            { "2000:ffff:ffff:ffff:ffff:ffff:ffff:ffff", false },
            { "2001::", true },
            { "2001:0:ffff:ffff:ffff:ffff:ffff:ffff", true },
            { "2001:2::", true },
            { "2001:2:0:ffff:ffff:ffff:ffff:ffff", true },
            { "2001:3::", true },
            { "2001:10::", true },
            { "2001:1f:ffff:ffff:ffff:ffff:ffff:ffff", true },
            { "2001:20::", true },
            { "2001:2f:ffff:ffff:ffff:ffff:ffff:ffff", true },
            { "2001:1ff:ffff:ffff:ffff:ffff:ffff:ffff", true },
            { "2001:200::", false },

            // 2001:db8::/32 documentation
            { "2001:db8::", true },
            { "2001:db8:ffff:ffff:ffff:ffff:ffff:ffff", true },
            { "2001:db9::", false },

            // 3fff::/20 documentation (RFC 9637)
            { "3ffe:ffff:ffff:ffff:ffff:ffff:ffff:ffff", false },
            { "3fff::", true },
            { "3fff:fff:ffff:ffff:ffff:ffff:ffff:ffff", true },
            { "3fff:1000::", false },

            // 2620:4f:8000::/48 AS112
            { "2620:4f:7fff:ffff:ffff:ffff:ffff:ffff", false },
            { "2620:4f:8000::", true },
            { "2620:4f:8000:ffff:ffff:ffff:ffff:ffff", true },
            { "2620:4f:8001::", false },

            // 2002::/16 6to4
            { "2001:ffff:ffff:ffff:ffff:ffff:ffff:ffff", false },
            { "2002::", true },
            { "2002:ffff:ffff:ffff:ffff:ffff:ffff:ffff", true },
            { "2003::", false },

            // ordinary public addresses stay reachable
            { "8.8.8.8", false },
            { "1.1.1.1", false },
            { "2606:4700:4700::1111", false },
            { "2001:4860:4860::8888", false },
        };

    [Theory]
    [MemberData(nameof(Edges))]
    public void Range_edges(string address, bool forbidden) =>
        Assert.Equal(forbidden, AddressGuard.IsForbidden(IPAddress.Parse(address)));

    [Fact]
    public void Ipv4_mapped_edges_follow_the_ipv4_table()
    {
        Assert.True(AddressGuard.IsForbidden(IPAddress.Parse("::ffff:192.88.99.7")));
        Assert.False(AddressGuard.IsForbidden(IPAddress.Parse("::ffff:192.88.100.7")));
    }
}
