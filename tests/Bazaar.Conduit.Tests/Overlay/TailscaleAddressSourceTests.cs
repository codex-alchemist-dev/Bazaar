using System.Net;
using Bazaar.Conduit;

namespace Bazaar.Conduit.Tests.Overlay;

/// <summary>Parser tests only. The real <c>tailscale</c> process is never started here.</summary>
public sealed class TailscaleAddressSourceTests
{
    [Theory]
    [InlineData("100.64.0.5\n", "100.64.0.5")]
    [InlineData("100.64.0.5", "100.64.0.5")]
    [InlineData("100.64.0.5\r\n", "100.64.0.5")]
    [InlineData("\n100.64.0.5\n", "100.64.0.5")]
    public void Parses_the_first_ipv4_line(string output, string expected)
    {
        var address = TailscaleAddressSource.ParseIPv4Output(output);

        Assert.Equal(IPAddress.Parse(expected), address);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n")]
    [InlineData(null)]
    [InlineData("garbage")]
    [InlineData("Error: not logged in\n")]
    [InlineData("100.64.0.5 extra\n")]
    [InlineData("1.2.3\n")]
    [InlineData("fd7a:115c:a1e0::1\n")]
    public void Garbage_and_empty_output_gives_null(string? output)
    {
        Assert.Null(TailscaleAddressSource.ParseIPv4Output(output));
    }
}
