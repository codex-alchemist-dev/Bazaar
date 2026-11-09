using Bazaar.Server.Transports;
using Bazaar.Server.Transports.Tests.Support;

namespace Bazaar.Server.Transports.Tests.Contract;

public sealed class OfferRulesTests
{
    private static TransportOffer Make(string uri, TransportScope scope, string hash = "") =>
        new("m", scope, new Uri(uri), string.IsNullOrEmpty(hash) ? Contexts.KeyHash : hash, null, false, true);

    [Fact]
    public void Wss_offer_with_pinned_hash_is_accepted()
    {
        Assert.Null(TransportOfferRules.Validate(Make("wss://192.168.1.20:7443/ws", TransportScope.Lan)));
    }

    [Fact]
    public void Plain_ws_is_accepted_only_on_loopback()
    {
        Assert.Null(TransportOfferRules.Validate(Make("ws://127.0.0.1:7443/ws", TransportScope.Loopback)));
        Assert.NotNull(TransportOfferRules.Validate(Make("ws://10.0.0.5:7443/ws", TransportScope.Lan)));
    }

    [Fact]
    public void Missing_or_non_sha256_hash_is_rejected()
    {
        Assert.NotNull(TransportOfferRules.Validate(Make("wss://a.example.test/ws", TransportScope.Internet, "md5:abc")));
    }

    [Fact]
    public void URI_with_user_info_is_rejected()
    {
        Assert.NotNull(TransportOfferRules.Validate(Make("wss://user:secret@a.example.test/ws", TransportScope.Internet)));
    }

    [Fact]
    public void URI_with_token_query_is_rejected()
    {
        Assert.NotNull(TransportOfferRules.Validate(Make("wss://a.example.test/ws?token=abc", TransportScope.Internet)));
    }

    [Fact]
    public void Non_websocket_scheme_is_rejected()
    {
        Assert.NotNull(TransportOfferRules.Validate(Make("https://a.example.test/ws", TransportScope.Internet)));
    }
}
