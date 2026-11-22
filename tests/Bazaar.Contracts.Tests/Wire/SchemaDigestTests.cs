using System.Security.Cryptography;
using System.Text;
using Bazaar.Contracts.Wire;

namespace Bazaar.Contracts.Tests.Wire;

public class SchemaDigestTests
{
    private static string Hash(string canonical) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));

    [Fact]
    public void Digest_is_over_the_sorted_pairs_joined_by_newline()
    {
        var id = SchemaDigest.Compute([("linux", 2), ("common", 9)]);
        Assert.Equal(Hash("common:9\nlinux:2"), id);
    }

    [Fact]
    public void Only_the_highest_number_of_each_namespace_counts_and_order_does_not_matter()
    {
        var a = SchemaDigest.Compute([("common", 1), ("common", 3), ("jobs", 1), ("common", 2)]);
        var b = SchemaDigest.Compute([("jobs", 1), ("common", 3)]);
        Assert.Equal(a, b);
    }

    [Fact]
    public void A_higher_migration_or_an_extra_namespace_changes_the_identity()
    {
        var baseline = SchemaDigest.Compute([("common", 3)]);
        Assert.NotEqual(baseline, SchemaDigest.Compute([("common", 4)]));
        Assert.NotEqual(baseline, SchemaDigest.Compute([("common", 3), ("jobs", 1)]));
    }

    [Fact]
    public void Sort_is_ordinal()
    {
        // Culture-sensitive sorting could put "B" before "a"; ordinal puts upper case first.
        Assert.Equal(Hash("B:1\na:1"), SchemaDigest.Compute([("a", 1), ("B", 1)]));
    }

    [Fact]
    public void Empty_input_hashes_the_empty_text()
    {
        Assert.Equal(Hash(""), SchemaDigest.Compute([]));
    }

    [Fact]
    public void Identity_is_prefixed_lowercase_hex()
    {
        var id = SchemaDigest.Compute([("common", 1)]);
        Assert.StartsWith(SchemaDigest.Prefix, id, StringComparison.Ordinal);
        Assert.Equal(SchemaDigest.Prefix.Length + 64, id.Length);
        Assert.Equal(id, id.ToLowerInvariant());
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("a:b")]
    [InlineData("a\nb")]
    public void Bad_namespaces_are_refused(string ns) =>
        Assert.Throws<ArgumentException>(() => SchemaDigest.Compute([(ns, 1)]));

    [Fact]
    public void Negative_numbers_are_refused() =>
        Assert.Throws<ArgumentException>(() => SchemaDigest.Compute([("common", -1)]));
}
