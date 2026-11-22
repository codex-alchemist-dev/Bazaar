using Bazaar.Server.VmEgress.Matching;

namespace Bazaar.Server.VmEgress.Tests;

public class AllowlistMatcherTests
{
    private static Allowlist Compile(params string[] entries)
    {
        Assert.True(Allowlist.TryCompile(entries, out var list, out var bad), $"entry rejected: {bad}");
        return list;
    }

    [Theory]
    [InlineData("example.com", "example.com", 443, true)]
    [InlineData("example.com", "EXAMPLE.COM", 443, true)]
    [InlineData("EXAMPLE.com", "example.com", 80, true)]
    [InlineData("example.com", "example.com.", 443, true)]
    [InlineData("example.com", "example.com", 8443, false)]
    [InlineData("example.com", "example.com", 22, false)]
    [InlineData("example.com:8443", "example.com", 8443, true)]
    [InlineData("example.com:8443", "example.com", 443, false)]
    [InlineData("example.com", "www.example.com", 443, false)]
    [InlineData("example.com", "evil-example.com", 443, false)]
    [InlineData("example.com", "example.com.evil.com", 443, false)]
    [InlineData("example.com", "xexample.com", 443, false)]
    [InlineData("*.example.com", "www.example.com", 443, true)]
    [InlineData("*.example.com", "a.b.example.com", 443, true)]
    [InlineData("*.example.com", "WWW.Example.COM.", 443, true)]
    [InlineData("*.example.com", "example.com", 443, false)]
    [InlineData("*.example.com", "evil-example.com", 443, false)]
    [InlineData("*.example.com", "wwwexample.com", 443, false)]
    [InlineData("*.example.com", "www.example.com.evil.com", 443, false)]
    [InlineData("*.example.com", "evilexample.com", 443, false)]
    [InlineData("*.example.com", "www.example.com", 25, false)]
    [InlineData("*.example.com:8443", "www.example.com", 8443, true)]
    public void Matches_whole_labels_only(string entry, string host, int port, bool expected)
    {
        Assert.Equal(expected, Compile(entry).IsAllowed(host, port));
    }

    [Theory]
    [InlineData("example.com@evil.com")]
    [InlineData("user:pass@example.com")]
    [InlineData("example.com/")]
    [InlineData("example.com\\evil.com")]
    [InlineData("example.com:443")] // a port must come in as a port, not inside the host
    [InlineData("example.com..")]
    [InlineData("..example.com")]
    [InlineData("")]
    [InlineData(" example.com")]
    [InlineData("exa mple.com")]
    [InlineData("example.com%00.evil.com")]
    [InlineData("example_com.evil.com")]
    [InlineData("-example.com")]
    public void Hostile_host_strings_never_match(string host)
    {
        var list = Compile("example.com", "*.example.com");

        Assert.False(list.IsAllowed(host, 443));
    }

    [Theory]
    [InlineData("exаmple.com")] // Cyrillic a
    [InlineData("ехample.com")] // Cyrillic e and x
    [InlineData("xn--exmple-4nf.com")]
    public void Punycode_lookalikes_do_not_match_the_ascii_name(string host)
    {
        var list = Compile("example.com", "*.example.com");

        Assert.False(list.IsAllowed(host, 443));
    }

    [Fact]
    public void An_idn_entry_matches_its_unicode_and_punycode_spellings()
    {
        var list = Compile("bücher.example");

        Assert.True(list.IsAllowed("bücher.example", 443));
        Assert.True(list.IsAllowed("BÜCHER.example", 443));
        Assert.True(list.IsAllowed("xn--bcher-kva.example", 443));
        Assert.False(list.IsAllowed("bucher.example", 443));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("8.8.8.8")]
    [InlineData("[::1]")]
    [InlineData("[2606:4700::1111]")]
    [InlineData("2130706433")]
    [InlineData("0x7f000001")]
    [InlineData("0x7f.1")]
    [InlineData("127.1")]
    [InlineData("0177.0.0.1")]
    [InlineData("1.2.3")]
    public void Ip_literals_are_refused_unless_listed(string host)
    {
        var list = Compile("example.com", "*.example.com");

        Assert.False(list.IsAllowed(host, 443));
    }

    [Fact]
    public void A_listed_ip_literal_matches_only_that_address()
    {
        var list = Compile("8.8.8.8", "[2606:4700::1111]:8443");

        Assert.True(list.IsAllowed("8.8.8.8", 443));
        Assert.False(list.IsAllowed("8.8.8.9", 443));
        Assert.True(list.IsAllowed("[2606:4700::1111]", 8443));
        Assert.False(list.IsAllowed("[2606:4700::1111]", 443));
    }

    [Theory]
    [InlineData("2130706433")]
    [InlineData("0x7f.1")]
    [InlineData("127.1")]
    [InlineData("0177.0.0.1")]
    public void Odd_numeric_forms_cannot_be_listed_either(string entry)
    {
        Assert.False(Allowlist.TryCompile([entry], out _, out var bad));
        Assert.Equal(entry, bad);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("*.com")]
    [InlineData("*.")]
    [InlineData("*example.com")]
    [InlineData("a.*.example.com")]
    [InlineData("**.example.com")]
    [InlineData("*.127.0.0.1")]
    [InlineData("example.com:0")]
    [InlineData("example.com:65536")]
    [InlineData("example.com:")]
    [InlineData("example.com:80:80")]
    [InlineData("example.com:8o")]
    [InlineData("https://example.com")]
    public void Bad_entries_are_rejected_not_skipped(string entry)
    {
        Assert.False(Allowlist.TryCompile(["example.org", entry], out var list, out var bad));
        Assert.Equal(entry, bad);
        Assert.Empty(list.Entries);
    }

    [Fact]
    public void Canonical_form_is_stable_and_keeps_ports_distinct()
    {
        Assert.True(AllowlistEntry.TryParse("EXAMPLE.com:443", out var withPort));
        Assert.True(AllowlistEntry.TryParse("example.com", out var withoutPort));
        Assert.Equal("example.com:443", withPort!.Canonical);
        Assert.Equal("example.com", withoutPort!.Canonical);
    }

    [Theory]
    [InlineData("example.com", "example.com", false)]
    [InlineData("example.com", "example.com:443", false)]
    [InlineData("example.com:443", "example.com", true)]
    [InlineData("example.com", "example.com:8443", true)]
    [InlineData("example.com", "*.example.com", true)]
    [InlineData("*.example.com", "a.example.com", false)]
    [InlineData("*.example.com", "a.b.example.com", false)]
    [InlineData("*.example.com", "*.b.example.com", false)]
    [InlineData("*.b.example.com", "*.example.com", true)]
    [InlineData("*.example.com", "example.com", true)]
    [InlineData("*.example.com", "evil-example.com", true)]
    [InlineData("8.8.8.8", "8.8.8.8", false)]
    [InlineData("8.8.8.8", "8.8.4.4", true)]
    public void Additions_tells_growth_from_a_shrink(string current, string proposed, bool grows)
    {
        var added = Compile(current).Additions(Compile(proposed));

        Assert.Equal(grows, added.Count > 0);
    }

    [Fact]
    public void Removing_everything_is_never_growth()
    {
        Assert.Empty(Compile("example.com", "*.github.com").Additions(Allowlist.Empty));
    }

    [Fact]
    public void Authority_parsing_splits_host_and_port()
    {
        Assert.True(RequestTarget.TryParseAuthority("Example.COM:8080", 443, out var host, out var port));
        Assert.Equal("example.com", host.Name);
        Assert.Equal(8080, port);

        Assert.True(RequestTarget.TryParseAuthority("example.com", 443, out _, out var defaulted));
        Assert.Equal(443, defaulted);
    }

    [Theory]
    [InlineData("example.com@evil.com:443")]
    [InlineData("example.com:443@evil.com")]
    [InlineData("example.com:99999")]
    [InlineData("example.com:-1")]
    [InlineData("example.com:0")]
    [InlineData("example.com:1:2")]
    [InlineData("[::1")]
    [InlineData("[::1]x")]
    [InlineData("example.com/path")]
    [InlineData("")]
    public void Authority_parsing_refuses_ambiguity(string authority)
    {
        Assert.False(RequestTarget.TryParseAuthority(authority, 443, out _, out _));
    }

    [Theory]
    [InlineData("http://example.com/a?b=c", "example.com", 80, "/a?b=c")]
    [InlineData("HTTP://Example.COM:8080", "example.com", 8080, "/")]
    [InlineData("http://example.com?x=1", "example.com", 80, "/?x=1")]
    public void Absolute_http_targets_split_into_authority_and_path(string target, string host, int port, string path)
    {
        Assert.True(RequestTarget.TryParseAbsoluteHttp(target, out var h, out var p, out var pq));
        Assert.Equal(host, h.Name);
        Assert.Equal(port, p);
        Assert.Equal(path, pq);
    }

    [Theory]
    [InlineData("http://user@example.com/")]
    [InlineData("http://example.com@evil.com/")]
    [InlineData("http://example.com:80@evil.com/")]
    [InlineData("https://example.com/")]
    [InlineData("/relative")]
    [InlineData("http:///path")]
    [InlineData("http://example.com/a b")]
    [InlineData("http://example.com/a#frag")]
    [InlineData("ftp://example.com/")]
    public void Absolute_http_parsing_refuses_userinfo_and_odd_targets(string target)
    {
        Assert.False(RequestTarget.TryParseAbsoluteHttp(target, out _, out _, out _));
    }
}
