using System.Text;

namespace Bazaar.Server.VmEgress.Tests;

/// <summary>End to end: a CONNECT to a name is pinned to the TLS server name the allowlist approved.</summary>
public class SniPinningTests
{
    private static readonly TimeSpan _wait = TimeSpan.FromSeconds(5);

    private static async Task<RawClient> OpenTunnelAsync(ProxyRig rig, string target = "example.com:443")
    {
        var client = await RawClient.ConnectAsync(rig.Endpoint);
        await client.SendAsync($"CONNECT {target} HTTP/1.1\r\n\r\n");
        Assert.Contains("200 Connection Established", await client.ReadUntilAsync("\r\n\r\n", _wait), StringComparison.Ordinal);
        return client;
    }

    private static ProxyRig Rig(bool echo = true, params string[] allow) =>
        ProxyRig.Build(echo, allow.Length == 0 ? ["example.com"] : allow, r => r.Answer("example.com", ["8.8.8.8"]).Answer("xn--bcher-kva.example", ["8.8.8.8"]).Answer("a.example.com", ["8.8.8.8"]));

    [Fact]
    public async Task A_matching_sni_is_relayed_once_and_in_order()
    {
        await using var rig = Rig();
        using var client = await OpenTunnelAsync(rig);
        var hello = HelloBuilder.Hello("example.com");

        await client.SendBytesAsync(hello);

        Assert.Equal(hello, await client.ReadExactAsync(hello.Length, _wait));
        Assert.Empty(rig.DenyReasons);
    }

    [Theory]
    [InlineData("EXAMPLE.COM")]
    [InlineData("example.com.")]
    public async Task Case_and_a_trailing_dot_normalise_to_the_same_name(string sni)
    {
        await using var rig = Rig();
        using var client = await OpenTunnelAsync(rig);

        var hello = HelloBuilder.Hello(sni);
        await client.SendBytesAsync(hello);

        Assert.Equal(hello, await client.ReadExactAsync(hello.Length, _wait));
        Assert.Empty(rig.DenyReasons);
    }

    [Fact]
    public async Task A_hello_split_over_three_records_with_the_right_name_goes_through()
    {
        await using var rig = Rig();
        using var client = await OpenTunnelAsync(rig);
        var wire = HelloBuilder.Split(HelloBuilder.Handshake(HelloBuilder.ServerNameExtension("example.com"), HelloBuilder.Extension(0x15, new byte[60])), 3);

        // Send it in awkward pieces so the proxy has to keep reading.
        await client.SendBytesAsync(wire[..7]);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        await client.SendBytesAsync(wire[7..]);

        Assert.Equal(wire, await client.ReadExactAsync(wire.Length, _wait));
        Assert.Empty(rig.DenyReasons);
    }

    [Fact]
    public async Task An_idn_connect_matches_its_punycode_sni()
    {
        await using var rig = Rig(true, "xn--bcher-kva.example");
        using var client = await OpenTunnelAsync(rig, "bücher.example:443");

        var hello = HelloBuilder.Hello("xn--bcher-kva.example");
        await client.SendBytesAsync(hello);

        Assert.Equal(hello, await client.ReadExactAsync(hello.Length, _wait));
        Assert.Empty(rig.DenyReasons);
    }

    [Theory]
    [InlineData("evil.test", "sni-mismatch")]
    [InlineData("a.example.com", "sni-mismatch")]
    [InlineData("example.com.evil.test", "sni-mismatch")]
    [InlineData("8.8.8.8", "sni-mismatch")]
    [InlineData("example.com\u0000.evil.test", "tls-malformed")]
    [InlineData("bücher.example", "tls-malformed")]
    public async Task A_different_sni_is_denied_and_nothing_reaches_the_upstream(string sni, string reason)
    {
        await using var rig = Rig();
        using var client = await OpenTunnelAsync(rig);

        await client.SendBytesAsync(HelloBuilder.Hello(sni));
        var (text, closed) = await client.ReadToEndAsync(_wait);

        Assert.True(closed);
        Assert.Equal("", text);
        Assert.Contains(reason, Assert.Single(rig.DenyReasons), StringComparison.Ordinal);
        Assert.True(await ProxyRig.EventuallyAsync(() => rig.Upstream.ClosedByPeer == 1));
        Assert.Equal(1, rig.Upstream.Connections); // connected at admit time, but never sent a byte
    }

    [Fact]
    public async Task A_mismatch_in_a_later_record_is_caught_before_any_byte_is_relayed()
    {
        await using var rig = Rig();
        using var client = await OpenTunnelAsync(rig);
        var wire = HelloBuilder.Split(HelloBuilder.Handshake(HelloBuilder.ServerNameExtension("evil.test")), 3);

        await client.SendBytesAsync(wire);
        var (text, closed) = await client.ReadToEndAsync(_wait);

        Assert.True(closed);
        Assert.Equal("", text);
        Assert.Contains("sni-mismatch", Assert.Single(rig.DenyReasons), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_hello_without_sni_is_denied()
    {
        await using var rig = Rig();
        using var client = await OpenTunnelAsync(rig);

        await client.SendBytesAsync(HelloBuilder.Hello());
        var (text, closed) = await client.ReadToEndAsync(_wait);

        Assert.True(closed);
        Assert.Equal("", text);
        Assert.Contains("sni-missing", Assert.Single(rig.DenyReasons), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ech_is_denied_even_with_the_right_outer_name()
    {
        await using var rig = Rig();
        using var client = await OpenTunnelAsync(rig);
        var wire = HelloBuilder.Record(HelloBuilder.Handshake(HelloBuilder.ServerNameExtension("example.com"), HelloBuilder.Extension(0xfe0d, [0, 1, 2, 3, 4])));

        await client.SendBytesAsync(wire);
        var (text, closed) = await client.ReadToEndAsync(_wait);

        Assert.True(closed);
        Assert.Equal("", text);
        Assert.Contains("ech-not-allowed", Assert.Single(rig.DenyReasons), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_malformed_hello_is_denied()
    {
        await using var rig = Rig();
        using var client = await OpenTunnelAsync(rig);

        await client.SendBytesAsync([0x16, 3, 1, 0, 4, 2, 0, 0, 0]); // a ServerHello-typed message from a client

        var (_, closed) = await client.ReadToEndAsync(_wait);
        Assert.True(closed);
        Assert.Contains("tls-malformed", Assert.Single(rig.DenyReasons), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SSH-2.0-test\r\n")]
    [InlineData("hello plain tcp")]
    public async Task Non_tls_protocols_are_relayed_unchanged(string payload)
    {
        await using var rig = Rig(true, "example.com");
        using var client = await OpenTunnelAsync(rig);

        await client.SendAsync(payload);

        Assert.Contains(payload, await client.ReadUntilAsync(payload, _wait), StringComparison.Ordinal);
        Assert.Empty(rig.DenyReasons);
    }

    [Fact]
    public async Task An_sslv2_style_hello_is_not_tls_and_is_relayed_unchanged()
    {
        await using var rig = Rig();
        using var client = await OpenTunnelAsync(rig);
        byte[] v2 = [0x80, 0x1c, 0x01, 0x03, 0x01, 0x00, 0x03, 0x00, 0x00, 0x00, 0x10];

        await client.SendBytesAsync(v2);

        Assert.Equal(v2, await client.ReadExactAsync(v2.Length, _wait));
        Assert.Empty(rig.DenyReasons);
    }

    [Fact]
    public async Task A_hello_pipelined_behind_the_connect_head_is_checked_too()
    {
        await using var rig = Rig();
        using var client = await RawClient.ConnectAsync(rig.Endpoint);

        await client.SendAsync("CONNECT example.com:443 HTTP/1.1\r\n\r\n" + Encoding.Latin1.GetString(HelloBuilder.Hello("evil.test")));
        var (text, closed) = await client.ReadToEndAsync(_wait);

        Assert.True(closed);
        Assert.StartsWith("HTTP/1.1 200", text, StringComparison.Ordinal);
        Assert.DoesNotContain("evil.test", text, StringComparison.Ordinal);
        Assert.Contains("sni-mismatch", Assert.Single(rig.DenyReasons), StringComparison.Ordinal);
        Assert.True(await ProxyRig.EventuallyAsync(() => rig.Upstream.ClosedByPeer == 1));
    }

    [Fact]
    public async Task A_connect_to_an_ip_literal_is_not_sni_checked()
    {
        await using var rig = ProxyRig.Build(true, ["8.8.8.8"]);
        using var client = await OpenTunnelAsync(rig, "8.8.8.8:443");
        var hello = HelloBuilder.Hello("whatever.test");

        await client.SendBytesAsync(hello);

        Assert.Equal(hello, await client.ReadExactAsync(hello.Length, _wait));
        Assert.Empty(rig.DenyReasons);
    }

    [Fact]
    public async Task A_hello_that_never_completes_is_closed_by_the_timeout()
    {
        await using var rig = ProxyRig.Build(true, ["example.com"], r => r.Answer("example.com", ["8.8.8.8"]), new VmEgressOptions { HeaderTimeout = TimeSpan.FromMilliseconds(300) });
        using var client = await OpenTunnelAsync(rig);

        await client.SendBytesAsync(HelloBuilder.Hello("example.com")[..20]);
        var (text, closed) = await client.ReadToEndAsync(_wait);

        Assert.True(closed);
        Assert.Equal("", text);
        Assert.True(await ProxyRig.EventuallyAsync(() => rig.Upstream.ClosedByPeer == 1));
    }

    [Fact]
    public async Task A_refused_tunnel_leaves_no_connection_open()
    {
        await using var rig = Rig();
        using var client = await OpenTunnelAsync(rig);
        await client.SendBytesAsync(HelloBuilder.Hello("evil.test"));
        _ = await client.ReadToEndAsync(_wait);

        Assert.True(await ProxyRig.EventuallyAsync(() => rig.Proxy.ActiveConnections == 0));
    }

    [Fact]
    public async Task A_hello_dribbled_past_the_timeout_is_cut_off_before_anything_is_relayed()
    {
        await using var rig = ProxyRig.Build(true, ["example.com"], r => r.Answer("example.com", ["8.8.8.8"]), new VmEgressOptions { HeaderTimeout = TimeSpan.FromMilliseconds(400) });
        using var client = await OpenTunnelAsync(rig);
        var hello = HelloBuilder.Hello("example.com");

        var cut = false;
        for (var i = 0; i < hello.Length - 1 && !cut; i++)
        {
            try
            {
                await client.SendBytesAsync(hello[i..(i + 1)]);
                await Task.Delay(60, TestContext.Current.CancellationToken);
            }
            catch (IOException)
            {
                cut = true;
            }
        }

        var (text, closed) = await client.ReadToEndAsync(_wait);
        Assert.True(closed);
        Assert.Equal("", text);
        Assert.True(await ProxyRig.EventuallyAsync(() => rig.Upstream.ClosedByPeer == 1));
    }

    [Fact]
    public async Task Leftover_bytes_larger_than_the_tls_cap_are_relayed_whole()
    {
        await using var rig = ProxyRig.Build(true, ["example.com"], r => r.Answer("example.com", ["8.8.8.8"]), new VmEgressOptions { MaxHeaderBytes = 64 * 1024 });
        using var client = await RawClient.ConnectAsync(rig.Endpoint);
        var payload = new string('x', 40_000);

        await client.SendAsync("CONNECT example.com:443 HTTP/1.1\r\n\r\n" + payload);
        var head = await client.ReadUntilAsync("\r\n\r\n", _wait);

        var echoed = head[(head.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..];
        while (echoed.Length < payload.Length)
        {
            var more = await client.ReadExactAsync(payload.Length - echoed.Length, _wait);
            if (more.Length == 0)
            {
                break;
            }

            echoed += Encoding.ASCII.GetString(more);
        }

        Assert.Equal(payload, echoed);
        Assert.Empty(rig.DenyReasons);
    }
}
