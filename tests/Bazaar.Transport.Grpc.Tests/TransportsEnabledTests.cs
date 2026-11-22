using Bazaar.Server.Transport.Grpc;
using Bazaar.Wire.Abstractions;
using Microsoft.Extensions.Options;

namespace Bazaar.Transport.Grpc.Tests;

public class TransportsEnabledTests
{
    [Fact]
    public void Setting_key_is_the_doc_90_key() => Assert.Equal("wire.transports.enabled", WireTransportsEnabledOptions.SettingKey);

    [Theory]
    [InlineData("ws", true)]
    [InlineData("quic", true)]
    [InlineData("grpc", true)]
    [InlineData("grpc-web", false)]
    [InlineData("webtransport", false)]
    public void Unset_enables_everything_except_the_browser_transports(string transport, bool expected) =>
        Assert.Equal(expected, new WireTransportsEnabledOptions().IsEnabled(transport));

    [Fact]
    public void An_operator_list_enables_exactly_what_it_names()
    {
        var options = new WireTransportsEnabledOptions { Enabled = ["ws", "grpc-web"] };
        Assert.True(options.IsEnabled("grpc-web"));
        Assert.True(options.IsEnabled("ws"));
        Assert.False(options.IsEnabled("grpc"));
    }

    [Fact]
    public void Listener_offers_only_while_enabled_and_addressed()
    {
        var transport = Options.Create(new GrpcTransportOptions { PublicEndpoint = "https://host.example:5001", EnabledCodecs = ["json", "cbor"] });
        var listener = new GrpcTransportListener("grpc-web", transport, Options.Create(new WireTransportsEnabledOptions()));
        Assert.Empty(listener.Offers);

        var on = new GrpcTransportListener("grpc-web", transport, Options.Create(new WireTransportsEnabledOptions { Enabled = ["grpc-web"] }));
        var offer = Assert.Single(on.Offers);
        Assert.Equal("grpc-web", offer.Transport);
        Assert.Equal(["json", "cbor"], offer.Codecs);
        Assert.True(on.IsSupported);
    }
}
