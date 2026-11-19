using Bazaar.Server.BrowserPool.Abstractions;
using Bazaar.Server.BrowserPool.LinuxHost;

namespace Bazaar.Server.BrowserPool.LinuxHost.Tests;

public sealed class NftListingCheckTests
{
    private const string _full =
        "table inet bazaar_pool {\n\tchain output {\n\t\ttype filter hook output priority filter; policy accept;\n"
        + "\t\tmeta skuid 10001 ip daddr 10.0.0.2 tcp dport 3128 accept\n"
        + "\t\tmeta skuid >= 10000 meta skuid <= 59999 drop\n\t}\n}\n";

    [Fact]
    public void A_complete_listing_has_no_problems()
    {
        Assert.Empty(NftListingCheck.Problems(_full, "bazaar_pool", [new PoolFirewallRule(10001, "10.0.0.2", 3128)]));
    }

    [Fact]
    public void An_empty_listing_means_the_table_is_missing()
    {
        Assert.Contains("table", Assert.Single(NftListingCheck.Problems(string.Empty, "bazaar_pool", [])), StringComparison.Ordinal);
    }

    [Fact]
    public void Another_tables_listing_is_not_accepted()
    {
        Assert.NotEmpty(NftListingCheck.Problems(_full.Replace("bazaar_pool", "other", StringComparison.Ordinal), "bazaar_pool", []));
    }

    [Theory]
    [InlineData(1000, "10.0.0.2", 3128)]
    [InlineData(10001, "10.0.0.22", 3128)]
    [InlineData(10001, "10.0.0.2", 312)]
    [InlineData(10001, "10.0.0.2", 31280)]
    public void A_near_miss_accept_rule_does_not_count(int uid, string host, int port)
    {
        Assert.NotEmpty(NftListingCheck.Problems(_full, "bazaar_pool", [new PoolFirewallRule(uid, host, port)]));
    }

    [Fact]
    public void An_accept_line_is_not_a_drop_rule()
    {
        var onlyAccept = _full.Replace("\t\tmeta skuid >= 10000 meta skuid <= 59999 drop\n", string.Empty, StringComparison.Ordinal);
        Assert.Contains(NftListingCheck.Problems(onlyAccept, "bazaar_pool", []), p => p.Contains("drop", StringComparison.Ordinal));
    }
}
