using Bazaar.Wire.Abstractions;

namespace Bazaar.Wire.Abstractions.Tests;

public sealed class WireNegotiatorTests
{
    [Fact]
    public void PickCodec_FollowsClientOrder_AndNeverPicksUnoffered()
    {
        Assert.Equal("cbor", WireNegotiator.PickCodec(["cbor", "json"], ["json", "cbor"]));
        Assert.Equal("json", WireNegotiator.PickCodec(["msgpack", "json"], ["json", "cbor"]));
        Assert.Null(WireNegotiator.PickCodec(["msgpack"], ["json"]));
    }

    [Fact]
    public void OrderOffers_DropsUnsupported_AndSortsByClientPreference()
    {
        TransportOffer Offer(string t) => new(t, "x", null, ["json"]);
        var ordered = WireNegotiator.OrderOffers(
            [Offer("ws"), Offer("grpc"), Offer("quic"), Offer("weird")],
            ["quic", "grpc", "ws"],
            t => t != "grpc");
        Assert.Equal(["quic", "ws", "weird"], ordered.Select(o => o.Transport));
    }

    [Fact]
    public void Subprotocols_RoundTrip_AndIgnoreForeignNames()
    {
        var header = WireNegotiator.FormatSubprotocols(["cbor", "json"]);
        Assert.Equal("bazaar.v1.cbor, bazaar.v1.json", header);
        Assert.Equal(["cbor", "json"], WireNegotiator.ParseSubprotocols(header + ", graphql-ws, bazaar.v1."));
        Assert.Empty(WireNegotiator.ParseSubprotocols(null));
    }

    [Fact]
    public void OfferHash_ChangesWithOrderAndContent()
    {
        var a = WireNegotiator.OfferHash(["ws"], ["json", "cbor"]);
        Assert.Equal(a, WireNegotiator.OfferHash(["ws"], ["json", "cbor"]));
        Assert.NotEqual(a, WireNegotiator.OfferHash(["ws"], ["cbor", "json"]));
        Assert.NotEqual(a, WireNegotiator.OfferHash(["ws", "quic"], ["json", "cbor"]));
    }

    [Fact]
    public void Floor_IsWebSocketJson() => Assert.Equal(new WireProfile("ws", "json"), WireProfile.Floor);

    [Theory]
    [InlineData("quic", true)]
    [InlineData("webtransport", true)]
    [InlineData("ws", false)]
    [InlineData("grpc", false)]
    public void UdpBased(string transport, bool expected) => Assert.Equal(expected, WireNames.IsUdpBased(transport));
}
