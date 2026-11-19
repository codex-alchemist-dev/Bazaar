using Bazaar.Conduit;
using Bazaar.Conduit.Tests.Support;

namespace Bazaar.Conduit.Tests.Contract;

public sealed class OfferRulesTests
{
    private static ConduitOffer Make(string uri, ConduitScope scope, string hash = "") =>
        new("m", scope, new Uri(uri), string.IsNullOrEmpty(hash) ? Contexts.KeyHash : hash, null, false, true);

    [Fact]
    public void Wss_offer_with_pinned_hash_is_accepted()
    {
        Assert.Null(ConduitOfferRules.Validate(Make("wss://192.168.1.20:7443/ws", ConduitScope.Lan)));
    }

    [Fact]
    public void Plain_ws_is_accepted_only_on_loopback()
    {
        Assert.Null(ConduitOfferRules.Validate(Make("ws://127.0.0.1:7443/ws", ConduitScope.Loopback)));
        Assert.NotNull(ConduitOfferRules.Validate(Make("ws://10.0.0.5:7443/ws", ConduitScope.Lan)));
    }

    [Fact]
    public void Missing_or_non_sha256_hash_is_rejected()
    {
        Assert.NotNull(ConduitOfferRules.Validate(Make("wss://a.example.test/ws", ConduitScope.Internet, "md5:abc")));
    }

    [Fact]
    public void URI_with_user_info_is_rejected()
    {
        Assert.NotNull(ConduitOfferRules.Validate(Make("wss://user:secret@a.example.test/ws", ConduitScope.Internet)));
    }

    [Fact]
    public void URI_with_token_query_is_rejected()
    {
        Assert.NotNull(ConduitOfferRules.Validate(Make("wss://a.example.test/ws?token=abc", ConduitScope.Internet)));
    }

    [Fact]
    public void Non_websocket_scheme_is_rejected()
    {
        Assert.NotNull(ConduitOfferRules.Validate(Make("https://a.example.test/ws", ConduitScope.Internet)));
    }
}
