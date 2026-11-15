using Bazaar.Conduit.Cloudflared;

namespace Bazaar.Conduit.Tests.Cloudflared;

public sealed class CloudflaredUrlParserTests
{
    [Fact]
    public void Parses_the_quick_tunnel_host_from_a_fixture_line()
    {
        const string line = "2026-11-09T10:00:00Z INF |  https://quick-fox-1234.trycloudflare.com                                        |";

        Assert.Equal("quick-fox-1234.trycloudflare.com", CloudflaredUrlParser.TryParseQuickTunnelHost(line));
    }

    [Fact]
    public void Returns_the_first_quick_tunnel_host_when_a_line_has_two()
    {
        const string line = "https://first-1.trycloudflare.com and https://second-2.trycloudflare.com";

        Assert.Equal("first-1.trycloudflare.com", CloudflaredUrlParser.TryParseQuickTunnelHost(line));
    }

    [Theory]
    [InlineData("INF Starting tunnel")]
    [InlineData("https://example.com/path")]
    [InlineData("quick-fox-1234.trycloudflare.com without a scheme")]
    public void Returns_null_when_no_quick_tunnel_url_is_present(string line)
    {
        Assert.Null(CloudflaredUrlParser.TryParseQuickTunnelHost(line));
    }
}
