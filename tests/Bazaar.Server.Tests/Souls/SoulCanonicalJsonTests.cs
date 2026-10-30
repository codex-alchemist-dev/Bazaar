using Bazaar.Server.Souls;

namespace Bazaar.Server.Tests.Souls;

public sealed class SoulCanonicalJsonTests
{
    [Fact]
    public void Keys_are_sorted_and_whitespace_is_removed()
    {
        var text = SoulCanonicalJson.Canonicalize("{ \"b\": 1,\n  \"a\": { \"z\": true, \"c\": null } }");
        Assert.Equal("{\"a\":{\"c\":null,\"z\":true},\"b\":1}", text);
    }

    [Fact]
    public void Array_order_is_kept()
    {
        Assert.Equal("{\"xs\":[3,1,2]}", SoulCanonicalJson.Canonicalize("{\"xs\": [3, 1, 2]}"));
    }

    [Fact]
    public void Key_order_does_not_change_the_canonical_text()
    {
        var first = SoulCanonicalJson.Canonicalize("{\"tone\":\"dry\",\"verbosity\":2}");
        var second = SoulCanonicalJson.Canonicalize("{\n  \"verbosity\": 2,\n  \"tone\": \"dry\"\n}");
        Assert.Equal(first, second);
    }

    [Fact]
    public void Invalid_json_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => SoulCanonicalJson.Canonicalize("{not json"));
    }

    [Fact]
    public void Duplicate_property_names_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => SoulCanonicalJson.Canonicalize("{\"a\":1,\"a\":2}"));
    }

    [Fact]
    public void Content_hash_is_64_lowercase_hex_and_depends_on_both_parts()
    {
        var charter = new string('a', 64);
        var hash = SoulVersioningService.ContentHash(charter, "{}");
        Assert.Matches("^[0-9a-f]{64}$", hash);
        Assert.NotEqual(hash, SoulVersioningService.ContentHash(charter, "{\"x\":1}"));
        Assert.NotEqual(hash, SoulVersioningService.ContentHash(new string('b', 64), "{}"));
    }
}
