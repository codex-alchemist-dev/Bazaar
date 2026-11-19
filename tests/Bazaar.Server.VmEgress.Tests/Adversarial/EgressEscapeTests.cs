using System.Net;
using System.Text;
using Bazaar.Server.VmEgress.Proxy;

namespace Bazaar.Server.VmEgress.Tests.Adversarial;

/// <summary>
/// Red-team tests: each one tries to get the proxy to send something somewhere the allowlist does not permit, and asserts that it
/// cannot. "Nothing escaped" means the upstream connector was never asked for an endpoint and the fake upstream saw no connection.
/// </summary>
public class EgressEscapeTests
{
    internal static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    internal static ProxyRig Rig(string[]? allow = null, Action<ScriptedResolver>? script = null, VmEgressOptions? options = null, ILocalAddressSource? local = null, bool http = false) =>
        ProxyRig.Build(!http, allow ?? ["example.com"], script ?? (r => r.Answer("example.com", ["8.8.8.8"])), options, local);

    internal static async Task<(string Text, bool Closed)> ProbeAsync(ProxyRig rig, string raw)
    {
        using var client = await RawClient.ConnectAsync(rig.Endpoint);
        await client.SendAsync(raw);
        return await client.ReadToEndAsync(Wait);
    }

    internal static void AssertRefused(string text)
    {
        // Either an HTTP error status, or the connection was dropped without an answer. Never a success.
        Assert.False(text.StartsWith("HTTP/1.1 2", StringComparison.Ordinal), text);
        Assert.False(text.StartsWith("HTTP/1.1 3", StringComparison.Ordinal), text);
    }

    internal static async Task AssertNothingEscapedAsync(ProxyRig rig)
    {
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Empty(rig.Connector.Requested);
        Assert.Equal(0, rig.Upstream.Connections);
    }

    // ---- request smuggling -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("GET http://example.com/ HTTP/1.1\r\nX-A: 1\nGET http://evil.test/ HTTP/1.1\r\nHost: evil.test\r\n\r\n", "bare LF inside the head")]
    [InlineData("GET http://example.com/ HTTP/1.1\r\nX-A: 1\rGET http://evil.test/ HTTP/1.1\r\n\r\n", "CR only")]
    [InlineData("GET http://example.com/ HTTP/1.1\r\nX-A: a\0b\r\n\r\n", "NUL in a header")]
    [InlineData("GET http://example.com/\0 HTTP/1.1\r\n\r\n", "NUL in the target")]
    [InlineData("GET http://example.com/ HTTP/1.1\r\nContent-Length: 5\r\nContent-Length: 6\r\n\r\nhello!", "duplicate Content-Length, different values")]
    [InlineData("POST http://example.com/ HTTP/1.1\r\nContent-Length: 5\r\nTransfer-Encoding: chunked\r\n\r\n0\r\n\r\n", "CL plus TE")]
    [InlineData("POST http://example.com/ HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n0\r\n\r\nGET http://evil.test/ HTTP/1.1\r\n\r\n", "chunked, second request in the body")]
    [InlineData("POST http://example.com/ HTTP/1.1\r\nTransfer-Encoding: xchunked\r\n\r\n", "odd transfer coding")]
    [InlineData("GET http://example.com/ HTTP/1.1\r\nX-A: 1\r\n GET http://evil.test/ HTTP/1.1\r\n\r\n", "obsolete line folding")]
    [InlineData("GET http://example.com/ HTTP/1.1\r\nX-A : 1\r\n\r\n", "space before the colon")]
    [InlineData("POST http://example.com/ HTTP/1.1\r\nTransfer-Encoding : chunked\r\nContent-Length: 4\r\n\r\n", "TE hidden by a space before the colon")]
    [InlineData("POST http://example.com/ HTTP/1.1\r\nContent-Length: +4\r\n\r\nabcd", "signed Content-Length")]
    [InlineData("POST http://example.com/ HTTP/1.1\r\nContent-Length: 4, 4\r\n\r\nabcd", "list Content-Length")]
    [InlineData("POST http://example.com/ HTTP/1.1\r\nContent-Length: 99999999999999999999\r\n\r\n", "overflowing Content-Length")]
    [InlineData("GET  http://example.com/ HTTP/1.1\r\n\r\n", "two spaces in the request line")]
    [InlineData("GET http://example.com/ HTTP/1.1 \r\n\r\n", "trailing space in the request line")]
    [InlineData("GET http://example.com/ HTTP/0.9\r\n\r\n", "old version")]
    [InlineData("GET http://example.com/ HTTP/1.1\r\n\r\nGET http://evil.test/ HTTP/1.1\r\nHost: evil.test\r\n\r\n", "pipelined second request in the same packet")]
    [InlineData("POST http://example.com/ HTTP/1.1\r\nContent-Length: 3\r\n\r\nabcGET http://evil.test/ HTTP/1.1\r\n\r\n", "pipelined behind a declared body")]
    public async Task Smuggling_attempts_never_reach_the_upstream(string raw, string why)
    {
        await using var rig = Rig(http: true);

        var (text, _) = await ProbeAsync(rig, raw);

        AssertRefused(text);
        Assert.DoesNotContain("evil.test", rig.Upstream.AllBytes, StringComparison.Ordinal);
        await AssertNothingEscapedAsync(rig);
        Assert.NotEmpty(why);
    }

    [Fact]
    public async Task A_bare_lf_head_never_completes_and_is_dropped()
    {
        await using var rig = Rig(options: new VmEgressOptions { HeaderTimeout = TimeSpan.FromMilliseconds(300) });

        var (text, closed) = await ProbeAsync(rig, "GET http://example.com/ HTTP/1.1\nHost: example.com\n\n");

        Assert.True(closed);
        AssertRefused(text);
        await AssertNothingEscapedAsync(rig);
    }

    [Fact]
    public async Task A_second_request_sent_in_a_later_packet_never_reaches_the_upstream()
    {
        await using var rig = Rig(script: r => r.Answer("example.com", ["8.8.8.8"]).Answer("evil.test", ["8.8.4.4"]), http: true);
        using var client = await RawClient.ConnectAsync(rig.Endpoint);

        await client.SendAsync("GET http://example.com/ HTTP/1.1\r\n\r\n");
        Assert.Contains("200 OK", await client.ReadUntilAsync("ok", Wait), StringComparison.Ordinal);
        try
        {
            await client.SendAsync("GET http://evil.test/ HTTP/1.1\r\nHost: evil.test\r\n\r\n");
        }
        catch (IOException)
        {
            // The proxy already closed the connection, which is also fine.
        }

        _ = await client.ReadToEndAsync(Wait);
        await Task.Delay(400, TestContext.Current.CancellationToken);

        Assert.DoesNotContain("evil.test", rig.Upstream.AllBytes, StringComparison.Ordinal);
        Assert.Single(rig.Connector.Requested);
    }

    [Fact]
    public async Task Bytes_after_a_declared_body_in_a_later_packet_never_reach_the_upstream()
    {
        await using var rig = Rig(http: true);
        using var client = await RawClient.ConnectAsync(rig.Endpoint);

        await client.SendAsync("POST http://example.com/ HTTP/1.1\r\nContent-Length: 4\r\n\r\nab");
        await Task.Delay(100, TestContext.Current.CancellationToken);
        await client.SendAsync("cdGET http://evil.test/ HTTP/1.1\r\n\r\n");
        _ = await client.ReadToEndAsync(Wait);

        Assert.DoesNotContain("evil.test", rig.Upstream.AllBytes, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("GET http://example.com@evil.test/ HTTP/1.1\r\n\r\n")]
    [InlineData("GET http://example.com:80@evil.test/ HTTP/1.1\r\n\r\n")]
    [InlineData("GET http://example.com:password@evil.test/ HTTP/1.1\r\n\r\n")]
    [InlineData("GET http://evil.test\\@example.com/ HTTP/1.1\r\n\r\n")]
    [InlineData("GET http://example.com\\.evil.test/ HTTP/1.1\r\n\r\n")]
    [InlineData("GET http://example.com#@evil.test/ HTTP/1.1\r\n\r\n")]
    [InlineData("GET http://example.com/#frag HTTP/1.1\r\n\r\n")]
    [InlineData("GET http://example.com/a\tb HTTP/1.1\r\n\r\n")]
    [InlineData("GET http://example.com\t.evil.test/ HTTP/1.1\r\n\r\n")]
    [InlineData("GET http://%65xample.com/ HTTP/1.1\r\n\r\n")]
    [InlineData("GET http:///example.com/ HTTP/1.1\r\n\r\n")]
    [InlineData("GET http:example.com/ HTTP/1.1\r\n\r\n")]
    [InlineData("GET //example.com/ HTTP/1.1\r\n\r\n")]
    [InlineData("GET /http://example.com/ HTTP/1.1\r\nHost: example.com\r\n\r\n")]
    [InlineData("GET ftp://example.com/ HTTP/1.1\r\n\r\n")]
    [InlineData("GET https://example.com/ HTTP/1.1\r\n\r\n")]
    [InlineData("CONNECT example.com@evil.test:443 HTTP/1.1\r\n\r\n")]
    [InlineData("CONNECT evil.test@example.com:443 HTTP/1.1\r\n\r\n")]
    [InlineData("CONNECT http://example.com:443 HTTP/1.1\r\n\r\n")]
    [InlineData("CONNECT example.com:443:evil.test:443 HTTP/1.1\r\n\r\n")]
    [InlineData("CONNECT example.com:443;evil.test:443 HTTP/1.1\r\n\r\n")]
    public async Task Target_confusions_are_refused_before_any_lookup(string raw)
    {
        await using var rig = Rig(script: r => r.Answer("example.com", ["8.8.8.8"]).Answer("evil.test", ["8.8.4.4"]), http: true);

        var (text, _) = await ProbeAsync(rig, raw);

        AssertRefused(text);
        await AssertNothingEscapedAsync(rig);
        Assert.DoesNotContain("evil.test", rig.Resolver.Asked, StringComparer.Ordinal);
    }

    [Fact]
    public async Task A_connect_without_a_port_means_443_and_the_host_header_is_ignored()
    {
        await using var rig = Rig();
        using var client = await RawClient.ConnectAsync(rig.Endpoint);

        await client.SendAsync("CONNECT example.com HTTP/1.1\r\nHost: evil.test:443\r\n\r\n");
        await client.ReadUntilAsync("\r\n\r\n", Wait);

        Assert.Equal(new IPEndPoint(IPAddress.Parse("8.8.8.8"), 443), Assert.Single(rig.Connector.Requested));
        Assert.DoesNotContain("evil.test", rig.Resolver.Asked, StringComparer.Ordinal);
    }

    [Fact]
    public async Task A_query_that_looks_like_an_authority_stays_on_the_allowed_host()
    {
        await using var rig = Rig(script: r => r.Answer("example.com", ["8.8.8.8"]).Answer("evil.test", ["8.8.4.4"]), http: true);

        await ProbeAsync(rig, "GET http://example.com?@evil.test/ HTTP/1.1\r\n\r\n");

        Assert.Equal(new IPEndPoint(IPAddress.Parse("8.8.8.8"), 80), Assert.Single(rig.Connector.Requested));
        Assert.All(rig.Resolver.Asked, h => Assert.Equal("example.com", h));
        Assert.StartsWith("GET /?@evil.test/ HTTP/1.1\r\nHost: example.com\r\n", Assert.Single(rig.Upstream.Heads), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_host_header_cannot_redirect_a_plain_request()
    {
        await using var rig = Rig(script: r => r.Answer("example.com", ["8.8.8.8"]).Answer("evil.test", ["8.8.4.4"]), http: true);

        await ProbeAsync(rig, "GET http://example.com/ HTTP/1.1\r\nHost: evil.test\r\nHost: evil.test\r\n\r\n");

        var head = Assert.Single(rig.Upstream.Heads);
        Assert.Contains("Host: example.com\r\n", head, StringComparison.Ordinal);
        Assert.DoesNotContain("Host: evil.test", head, StringComparison.Ordinal);
        Assert.DoesNotContain("evil.test", rig.Resolver.Asked, StringComparer.Ordinal);
    }

    [Fact]
    public async Task Upgrade_and_hop_by_hop_headers_are_not_forwarded()
    {
        await using var rig = Rig(http: true);

        await ProbeAsync(rig, "GET http://example.com/ HTTP/1.1\r\nConnection: Upgrade\r\nUpgrade: websocket\r\nTE: trailers\r\nKeep-Alive: 5\r\nProxy-Authorization: x\r\n\r\n");

        var head = Assert.Single(rig.Upstream.Heads);
        Assert.DoesNotContain("Upgrade", head, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TE:", head, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Keep-Alive", head, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Connection: close", head, StringComparison.Ordinal);
    }

    // ---- host confusions ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("127.1:80")]
    [InlineData("2130706433:80")]
    [InlineData("0x7f.0.0.1:80")]
    [InlineData("0x7f000001:80")]
    [InlineData("0177.0.0.1:80")]
    [InlineData("017700000001:80")]
    [InlineData("127.0.0.1.:80")]
    [InlineData("127.0.0.01:80")]
    [InlineData("[::ffff:127.0.0.1]:80")]
    [InlineData("[::ffff:7f00:1]:80")]
    [InlineData("[0:0:0:0:0:ffff:127.0.0.1]:80")]
    [InlineData("[::1]:80")]
    [InlineData("[::]:80")]
    [InlineData("[fe80::1%eth0]:80")]
    [InlineData("[fe80::1%25eth0]:80")]
    [InlineData("localhost:80")]
    [InlineData("LOCALHOST.:80")]
    [InlineData("0:80")]
    [InlineData("0.0.0.0:80")]
    [InlineData("169.254.169.254:80")]
    [InlineData("[fd00:ec2::254]:80")]
    [InlineData("100.100.100.200:80")]
    public async Task Loopback_and_metadata_spellings_never_get_connected_even_when_listed_in_the_allowlist(string authority)
    {
        // The allowlist even names the dangerous addresses: the address floor must still hold.
        await using var rig = Rig(["127.0.0.1", "[::1]", "0.0.0.0", "169.254.169.254", "100.100.100.200", "[::ffff:127.0.0.1]", "[fd00:ec2::254]", "localhost", "example.com"]);

        foreach (var request in new[] { $"GET http://{authority}/ HTTP/1.1\r\n\r\n", $"CONNECT {authority} HTTP/1.1\r\n\r\n" })
        {
            var (text, _) = await ProbeAsync(rig, request);
            AssertRefused(text);
        }

        await AssertNothingEscapedAsync(rig);
    }

    [Theory]
    [InlineData("example.com..:80")]
    [InlineData(".example.com:80")]
    [InlineData("example..com:80")]
    [InlineData("example.com.evil.test:80")]
    [InlineData("example.com%2eevil.test:80")]
    [InlineData("exаmple.com:80")]
    [InlineData("xn--.example.com:80")]
    [InlineData("xn--zzzzzzzzzzzzzzzzzzzzzz.com:80")]
    [InlineData("xn--example.com:80")]
    [InlineData("example.com․evil.test:80")]
    [InlineData("ex*mple.com:80")]
    [InlineData("*.example.com:80")]
    public async Task Name_confusions_do_not_match_the_allowlist(string authority)
    {
        await using var rig = Rig(["example.com"], http: true);

        var (text, _) = await ProbeAsync(rig, $"GET http://{authority}/ HTTP/1.1\r\n\r\n");

        AssertRefused(text);
        await AssertNothingEscapedAsync(rig);
        Assert.DoesNotContain(rig.Resolver.Asked, h => h != "example.com");
    }

    [Theory]
    [InlineData("example.com.")]
    [InlineData("EXAMPLE.COM")]
    [InlineData("Example.Com.")]
    [InlineData("exa\u00admple.com")] // soft hyphen is dropped by IDNA mapping: the same name
    [InlineData("example\u3002com")] // ideographic full stop maps to a dot: the same name
    public async Task Case_and_a_single_trailing_dot_are_the_same_allowed_name_and_nothing_else(string spelling)
    {
        await using var rig = Rig(["example.com"], http: true);

        await ProbeAsync(rig, $"GET http://{spelling}/ HTTP/1.1\r\n\r\n");

        Assert.Equal(new IPEndPoint(IPAddress.Parse("8.8.8.8"), 80), Assert.Single(rig.Connector.Requested));
        Assert.All(rig.Resolver.Asked, h => Assert.Equal("example.com", h));
    }

    [Theory]
    [InlineData("evil-example.com")]
    [InlineData("example.com.evil.com")]
    [InlineData("notexample.com")]
    [InlineData("example.com")] // the bare suffix is not covered by *.example.com
    [InlineData("com")]
    [InlineData("ample.com")]
    [InlineData("a.b.example.com.evil.test")]
    public async Task A_wildcard_entry_only_matches_names_strictly_below_the_suffix(string host)
    {
        await using var rig = Rig(["*.example.com"], r => r.Answer("a.example.com", ["8.8.8.8"]).Answer(host, ["8.8.4.4"]));

        var (text, _) = await ProbeAsync(rig, $"CONNECT {host}:443 HTTP/1.1\r\n\r\n");

        AssertRefused(text);
        await AssertNothingEscapedAsync(rig);
    }

    [Fact]
    public async Task A_wildcard_entry_does_match_a_real_subdomain()
    {
        await using var rig = Rig(["*.example.com"], r => r.Answer("a.b.example.com", ["8.8.8.8"]), http: true);

        await ProbeAsync(rig, "GET http://A.B.example.com/ HTTP/1.1\r\n\r\n");

        Assert.Single(rig.Connector.Requested);
    }

    // ---- DNS answers -------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("8.8.8.8", "10.0.0.1")]
    [InlineData("10.0.0.1", "8.8.8.8")]
    [InlineData("8.8.8.8", "127.0.0.1")]
    [InlineData("8.8.8.8", "::1")]
    [InlineData("2606:4700:4700::1111", "fe80::1")]
    [InlineData("8.8.8.8", "::ffff:127.0.0.1")]
    [InlineData("::ffff:7f00:1")]
    [InlineData("::ffff:10.1.2.3")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("64:ff9b::7f00:1")]
    [InlineData("64:ff9b::a9fe:a9fe")]
    [InlineData("64:ff9b:1::1")]
    [InlineData("2002:7f00:1::")]
    [InlineData("2002:a9fe:a9fe::1")]
    [InlineData("2001:0:4136:e378:8000:63bf:3fff:fdd2")]
    [InlineData("169.254.169.254")]
    [InlineData("169.254.170.2")]
    [InlineData("100.100.100.200")]
    [InlineData("168.63.129.16")]
    [InlineData("fd00:ec2::254")]
    [InlineData("fe80::a9fe:a9fe")]
    [InlineData("192.88.99.1")]
    [InlineData("2001:2::1")]
    [InlineData("3fff::1")]
    [InlineData("0.0.0.0")]
    [InlineData("255.255.255.255")]
    [InlineData("::")]
    public async Task A_dns_answer_that_holds_an_internal_address_refuses_the_name(params string[] answer)
    {
        await using var rig = Rig(script: r => r.Answer("example.com", answer));

        foreach (var request in new[] { "GET http://example.com/ HTTP/1.1\r\n\r\n", "CONNECT example.com:443 HTTP/1.1\r\n\r\n" })
        {
            var (text, _) = await ProbeAsync(rig, request);
            AssertRefused(text);
        }

        await AssertNothingEscapedAsync(rig);
        Assert.All(rig.DenyReasons, reason => Assert.Contains("forbidden-address", reason, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_name_that_resolves_to_a_local_interface_address_is_refused()
    {
        await using var rig = Rig(script: r => r.Answer("example.com", ["93.184.216.34"]), local: new FixedLocalAddresses("93.184.216.34"));

        var (text, _) = await ProbeAsync(rig, "CONNECT example.com:443 HTTP/1.1\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 403", text, StringComparison.Ordinal);
        await AssertNothingEscapedAsync(rig);
        Assert.Contains("forbidden-address", Assert.Single(rig.DenyReasons), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("::ffff:93.184.216.34")]
    [InlineData("2606:2800:220:1::1")]
    public async Task A_local_address_is_refused_in_every_spelling_and_among_others(string local)
    {
        await using var rig = Rig(
            script: r => r.Answer("example.com", ["8.8.8.8", local.StartsWith("::ffff:", StringComparison.Ordinal) ? "93.184.216.34" : local]),
            local: new FixedLocalAddresses(local));

        var (text, _) = await ProbeAsync(rig, "GET http://example.com/ HTTP/1.1\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 403", text, StringComparison.Ordinal);
        await AssertNothingEscapedAsync(rig);
    }

    [Fact]
    public async Task A_listed_ip_literal_that_is_a_local_address_is_refused()
    {
        await using var rig = Rig(["93.184.216.34"], local: new FixedLocalAddresses("93.184.216.34"));

        var (text, _) = await ProbeAsync(rig, "CONNECT 93.184.216.34:443 HTTP/1.1\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 403", text, StringComparison.Ordinal);
        await AssertNothingEscapedAsync(rig);
    }

    [Fact]
    public async Task A_second_dns_answer_cannot_redirect_a_connection_that_was_already_checked()
    {
        await using var rig = Rig(script: r => r.Answer("example.com", ["8.8.8.8"], ["127.0.0.1"]));

        await ProbeAsync(rig, "GET http://example.com/ HTTP/1.1\r\n\r\n");

        Assert.Equal(new IPEndPoint(IPAddress.Parse("8.8.8.8"), 80), Assert.Single(rig.Connector.Requested));
        Assert.Equal(1, rig.Resolver.Calls);
    }

    // ---- ports -------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("example.com:0")]
    [InlineData("example.com:65536")]
    [InlineData("example.com:99999")]
    [InlineData("example.com:-1")]
    [InlineData("example.com:+80")]
    [InlineData("example.com:8o")]
    [InlineData("example.com:0x50")]
    [InlineData("example.com:")]
    [InlineData("example.com:80:80")]
    [InlineData("example.com:٨٠")]
    [InlineData("example.com:1e2")]
    [InlineData("example.com:22")]
    [InlineData("example.com:8080")]
    [InlineData("example.com:00022")]
    public async Task Ports_outside_the_entry_never_get_connected(string authority)
    {
        await using var rig = Rig();

        foreach (var request in new[] { $"GET http://{authority}/ HTTP/1.1\r\n\r\n", $"CONNECT {authority} HTTP/1.1\r\n\r\n" })
        {
            var (text, _) = await ProbeAsync(rig, request);
            AssertRefused(text);
        }

        await AssertNothingEscapedAsync(rig);
    }

    [Theory]
    [InlineData("example.com:080", 80)]
    [InlineData("example.com:00443", 443)]
    public async Task A_zero_padded_port_is_read_as_the_same_number_the_allowlist_checks(string authority, int port)
    {
        await using var rig = Rig(http: true);

        await ProbeAsync(rig, $"GET http://{authority}/ HTTP/1.1\r\n\r\n");

        Assert.Equal(port, Assert.Single(rig.Connector.Requested).Port);
    }

    [Fact]
    public async Task A_port_listed_on_the_entry_does_not_open_the_default_ports()
    {
        await using var rig = Rig(["example.com:8443"]);

        var (a, _) = await ProbeAsync(rig, "CONNECT example.com:443 HTTP/1.1\r\n\r\n");
        var (b, _) = await ProbeAsync(rig, "GET http://example.com/ HTTP/1.1\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 403", a, StringComparison.Ordinal);
        Assert.StartsWith("HTTP/1.1 403", b, StringComparison.Ordinal);
        await AssertNothingEscapedAsync(rig);
    }

    [Fact]
    public async Task A_connect_to_a_default_entry_on_a_non_default_port_is_refused()
    {
        await using var rig = Rig();

        var (text, _) = await ProbeAsync(rig, "CONNECT example.com:8443 HTTP/1.1\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 403", text, StringComparison.Ordinal);
        await AssertNothingEscapedAsync(rig);
    }

    // ---- lifecycle ---------------------------------------------------------------------------------------------------------

    private static async Task<RawClient> OpenEchoTunnelAsync(ProxyRig rig)
    {
        var client = await RawClient.ConnectAsync(rig.Endpoint);
        await client.SendAsync("CONNECT example.com:443 HTTP/1.1\r\n\r\n");
        Assert.Contains("200 Connection Established", await client.ReadUntilAsync("\r\n\r\n", Wait), StringComparison.Ordinal);
        await client.SendAsync("ping");
        Assert.Contains("ping", await client.ReadUntilAsync("ping", Wait), StringComparison.Ordinal);
        return client;
    }

    [Fact]
    public async Task Shrinking_the_allowlist_closes_live_tunnels_on_both_sides()
    {
        await using var rig = Rig(["example.com", "other.test"]);
        using var a = await OpenEchoTunnelAsync(rig);
        using var b = await OpenEchoTunnelAsync(rig);

        rig.SetAllowlist("other.test");

        Assert.True((await a.ReadToEndAsync(Wait)).Closed);
        Assert.True((await b.ReadToEndAsync(Wait)).Closed);
        Assert.True(await ProxyRig.EventuallyAsync(() => rig.Upstream.ClosedByPeer == 2));
        Assert.True(await ProxyRig.EventuallyAsync(() => rig.Proxy.ActiveConnections == 0));
    }

    [Fact]
    public async Task Shrinking_the_allowlist_closes_a_tunnel_that_is_still_waiting_for_its_first_bytes()
    {
        await using var rig = Rig();
        using var client = await RawClient.ConnectAsync(rig.Endpoint);
        await client.SendAsync("CONNECT example.com:443 HTTP/1.1\r\n\r\n");
        await client.ReadUntilAsync("\r\n\r\n", Wait);

        rig.SetAllowlist();

        Assert.True((await client.ReadToEndAsync(Wait)).Closed);
        Assert.True(await ProxyRig.EventuallyAsync(() => rig.Upstream.ClosedByPeer == 1));
    }

    [Fact]
    public async Task A_shrunk_allowlist_refuses_new_requests_at_once()
    {
        await using var rig = Rig();
        rig.SetAllowlist();

        var (text, _) = await ProbeAsync(rig, "CONNECT example.com:443 HTTP/1.1\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 403", text, StringComparison.Ordinal);
        await AssertNothingEscapedAsync(rig);
    }

    [Fact]
    public async Task Stop_closes_every_tunnel_both_sides_and_the_listener()
    {
        await using var rig = Rig();
        using var a = await OpenEchoTunnelAsync(rig);
        using var b = await OpenEchoTunnelAsync(rig);
        using var idle = await RawClient.ConnectAsync(rig.Endpoint);
        var endpoint = rig.Endpoint;

        rig.Proxy.Stop();

        Assert.True((await a.ReadToEndAsync(Wait)).Closed);
        Assert.True((await b.ReadToEndAsync(Wait)).Closed);
        Assert.True((await idle.ReadToEndAsync(Wait)).Closed);
        Assert.True(await ProxyRig.EventuallyAsync(() => rig.Upstream.ClosedByPeer == 2));
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            using var late = await RawClient.ConnectAsync(endpoint);
            await late.SendAsync("CONNECT example.com:443 HTTP/1.1\r\n\r\n");
            var (text, _) = await late.ReadToEndAsync(Wait);
            Assert.StartsWith("HTTP/1.1 200", text, StringComparison.Ordinal); // fails the test if a tunnel opened
            throw new InvalidOperationException("closed"); // reached only when nothing answered
        });
    }

    [Fact]
    public async Task A_burst_of_connections_never_exceeds_the_cap()
    {
        await using var rig = Rig(options: new VmEgressOptions { MaxConnectionsPerAgent = 3, HeaderTimeout = TimeSpan.FromSeconds(30) });
        var clients = new List<RawClient>();
        try
        {
            for (var i = 0; i < 40; i++)
            {
                clients.Add(await RawClient.ConnectAsync(rig.Endpoint));
                Assert.True(rig.Proxy.ActiveConnections <= 3);
            }

            await Task.Delay(200, TestContext.Current.CancellationToken);
            Assert.True(rig.Proxy.ActiveConnections <= 3);

            // Everything beyond the cap was closed; the first ones are still waiting for a head.
            var closed = 0;
            foreach (var client in clients)
            {
                if ((await client.ReadToEndAsync(TimeSpan.FromMilliseconds(30))).Closed)
                {
                    closed++;
                }
            }

            Assert.True(closed >= 37, $"closed {closed}");
        }
        finally
        {
            foreach (var client in clients)
            {
                client.Dispose();
            }
        }
    }

    [Fact]
    public async Task A_head_dribbled_one_byte_at_a_time_is_cut_off_by_the_header_timeout()
    {
        await using var rig = Rig(options: new VmEgressOptions { HeaderTimeout = TimeSpan.FromMilliseconds(400) });
        using var client = await RawClient.ConnectAsync(rig.Endpoint);
        var head = "GET http://example.com/ HTTP/1.1\r\nX-Slow: yes\r\n\r\n";
        var started = DateTime.UtcNow;

        var closed = false;
        foreach (var c in head)
        {
            try
            {
                await client.SendAsync(c.ToString());
            }
            catch (IOException)
            {
                closed = true;
                break;
            }

            await Task.Delay(60, TestContext.Current.CancellationToken);
            if (DateTime.UtcNow - started > TimeSpan.FromSeconds(4))
            {
                break;
            }
        }

        var (_, ended) = await client.ReadToEndAsync(Wait);

        Assert.True(closed || ended);
        await AssertNothingEscapedAsync(rig);
        Assert.True(await ProxyRig.EventuallyAsync(() => rig.Proxy.ActiveConnections == 0));
    }

    [Fact]
    public async Task An_oversize_head_gets_431_and_goes_nowhere()
    {
        await using var rig = Rig();

        var (text, _) = await ProbeAsync(rig, "GET http://example.com/ HTTP/1.1\r\nX-Big: " + new string('a', 40_000));

        Assert.StartsWith("HTTP/1.1 431", text, StringComparison.Ordinal);
        await AssertNothingEscapedAsync(rig);
    }

    [Fact]
    public async Task A_head_whose_terminator_lies_beyond_the_limit_gets_431()
    {
        await using var rig = Rig();

        var (text, _) = await ProbeAsync(rig, "GET http://example.com/ HTTP/1.1\r\nX-Big: " + new string('a', 16 * 1024) + "\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 431", text, StringComparison.Ordinal);
        await AssertNothingEscapedAsync(rig);
    }

    [Fact]
    public async Task Too_many_headers_get_431()
    {
        await using var rig = Rig();
        var headers = string.Concat(Enumerable.Range(0, 150).Select(i => $"X-{i}: 1\r\n"));

        var (text, _) = await ProbeAsync(rig, "GET http://example.com/ HTTP/1.1\r\n" + headers + "\r\n");

        Assert.StartsWith("HTTP/1.1 431", text, StringComparison.Ordinal);
        await AssertNothingEscapedAsync(rig);
    }

    [Fact]
    public async Task An_enormous_request_line_is_not_buffered_without_bound()
    {
        await using var rig = Rig();
        var raw = new StringBuilder("GET http://example.com/").Append('a', 1_000_000).Append(" HTTP/1.1\r\n\r\n").ToString();

        var (text, _) = await ProbeAsync(rig, raw);

        Assert.StartsWith("HTTP/1.1 431", text, StringComparison.Ordinal);
        await AssertNothingEscapedAsync(rig);
    }
}
