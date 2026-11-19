using Bazaar.Kodo.Export;

namespace Bazaar.Kodo.Tests.Export;

public sealed class KodoWebhookAllowListTests
{
    private static readonly KodoWebhookAllowList _list = new(["Hooks.Example.com", "*.example.net"]);

    [Fact]
    public void AnExactEntryMatchesIgnoringCase()
    {
        KodoWebhookDestinationDecision decision = _list.Evaluate("https://HOOKS.example.com/abc");

        Assert.True(decision.Allowed);
        Assert.Equal("allowed", decision.Reason);
    }

    [Fact]
    public void AWildcardMatchesSubdomainsButNotTheBareName()
    {
        Assert.True(_list.Evaluate("https://a.b.example.net/x").Allowed);
        Assert.False(_list.Evaluate("https://example.net/x").Allowed);
    }

    [Theory]
    [InlineData("https://badexample.net/x")]
    [InlineData("https://example.net.evil.test/x")]
    [InlineData("https://hooks.example.com.evil.test/x")]
    [InlineData("https://other.example.org/x")]
    public void NearMissesAreNotOnTheList(string url)
    {
        KodoWebhookDestinationDecision decision = _list.Evaluate(url);

        Assert.False(decision.Allowed);
        Assert.Equal("not_on_allow_list", decision.Reason);
    }

    [Fact]
    public void AnEmptyListAllowsNothing()
    {
        KodoWebhookDestinationDecision decision = KodoWebhookAllowList.Empty.Evaluate("https://hooks.example.com/x");

        Assert.False(decision.Allowed);
        Assert.Equal("not_on_allow_list", decision.Reason);
    }

    [Theory]
    [InlineData("http://hooks.example.com/x", "scheme_not_https")]
    [InlineData("https://user:pass@hooks.example.com/x", "userinfo_not_allowed")]
    [InlineData("https://hooks.example.com:8443/x", "port_not_allowed")]
    [InlineData("https://203.0.113.10/x", "ip_literal_not_allowed")]
    [InlineData("https://[2001:db8::1]/x", "ip_literal_not_allowed")]
    [InlineData("/relative/path", "not_absolute_url")]
    public void RefusesDestinationsThatBreakTheTransportRules(string url, string reason)
    {
        KodoWebhookDestinationDecision decision = _list.Evaluate(url);

        Assert.False(decision.Allowed);
        Assert.Equal(reason, decision.Reason);
    }

    [Fact]
    public void TheDecisionCarriesOnlyTheRedactedUrl()
    {
        KodoWebhookDestinationDecision allowed = _list.Evaluate("https://hooks.example.com/SECRET-PATH?token=SECRET-QUERY");
        KodoWebhookDestinationDecision refused = _list.Evaluate("https://user:SECRET-USER@other.test/SECRET-PATH");

        Assert.Equal("https://hooks.example.com/[redacted]", allowed.RedactedUrl);
        Assert.Equal("https://other.test/[redacted]", refused.RedactedUrl);
        Assert.DoesNotContain("SECRET", allowed.RedactedUrl, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET", refused.RedactedUrl, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://hooks.example.com")]
    [InlineData("hooks.example.com:443")]
    [InlineData("hooks.example.com/path")]
    [InlineData("*")]
    [InlineData("*.")]
    [InlineData("localhost")]
    [InlineData("203.0.113.10")]
    [InlineData("*.example.com.")]
    [InlineData("")]
    public void RefusesAnEntryThatIsNotAHostName(string entry)
    {
        Assert.Throws<ArgumentException>(() => new KodoWebhookAllowList([entry]));
    }

    [Fact]
    public void EntriesAreTrimmedAndCaseFolded()
    {
        KodoWebhookAllowList list = new([" Hooks.Example.COM "]);

        Assert.True(list.Evaluate("https://hooks.example.com/x").Allowed);
    }
}
