using System.Net;
using System.Net.Sockets;
using Bazaar.Server.VmEgress.Matching;
using Bazaar.Server.VmEgress.Proxy;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.VmEgress.Tests;

public class ProxyEndToEndTests
{
    private static readonly TimeSpan _wait = TimeSpan.FromSeconds(5);

    private sealed class Rig : IAsyncDisposable
    {
        public required AgentEgressProxy Proxy { get; init; }

        public required FakeUpstream Upstream { get; init; }

        public required ScriptedResolver Resolver { get; init; }

        public required RedirectingConnector Connector { get; init; }

        public required CapturingAuditLog Audit { get; init; }

        public IPEndPoint Endpoint => Proxy.LocalEndPoint!;

        public async ValueTask DisposeAsync()
        {
            await Proxy.DisposeAsync();
            await Upstream.DisposeAsync();
        }
    }

    private static Rig Build(bool echo, string[] allow, Action<ScriptedResolver>? script = null, VmEgressOptions? options = null, IReadOnlyList<IProxyRequestFilter>? filters = null)
    {
        var upstream = new FakeUpstream(echo);
        var resolver = new ScriptedResolver();
        script?.Invoke(resolver);
        var connector = new RedirectingConnector(upstream.EndPoint);
        var audit = new CapturingAuditLog();
        var opts = options ?? new VmEgressOptions();
        var auditor = new EgressAuditor(audit, opts, TimeProvider.System);
        Assert.True(Allowlist.TryCompile(allow, out var list, out _));
        var proxy = new AgentEgressProxy("agent-1", IPAddress.Loopback, 0, list, opts, auditor, filters, resolver, connector);
        proxy.Start();
        return new Rig { Proxy = proxy, Upstream = upstream, Resolver = resolver, Connector = connector, Audit = audit };
    }

    [Fact]
    public async Task Allowed_plain_request_is_forwarded_with_a_clean_head()
    {
        await using var rig = Build(false, ["example.com"], r => r.Answer("example.com", ["8.8.8.8"]));
        using var client = await RawClient.ConnectAsync(rig.Endpoint);

        await client.SendAsync("GET http://example.com/x?y=1 HTTP/1.1\r\nHost: evil.test\r\nProxy-Authorization: secret\r\nProxy-Connection: keep-alive\r\nX-Keep: yes\r\n\r\n");
        var (text, closed) = await client.ReadToEndAsync(_wait);

        Assert.Contains("200 OK", text, StringComparison.Ordinal);
        Assert.EndsWith("ok", text, StringComparison.Ordinal);
        Assert.True(closed);
        var head = Assert.Single(rig.Upstream.Heads);
        Assert.StartsWith("GET /x?y=1 HTTP/1.1\r\nHost: example.com\r\n", head, StringComparison.Ordinal);
        Assert.Contains("X-Keep: yes", head, StringComparison.Ordinal);
        Assert.Contains("Connection: close", head, StringComparison.Ordinal);
        Assert.DoesNotContain("evil.test", head, StringComparison.Ordinal);
        Assert.DoesNotContain("Proxy-", head, StringComparison.Ordinal);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("8.8.8.8"), 80), Assert.Single(rig.Connector.Requested));
        Assert.Single(rig.Audit.OfKind(EgressAuditor.AllowKind));
    }

    [Fact]
    public async Task Connect_to_an_allowed_host_opens_a_working_tunnel()
    {
        await using var rig = Build(true, ["example.com"], r => r.Answer("example.com", ["8.8.8.8"]));
        using var client = await RawClient.ConnectAsync(rig.Endpoint);

        await client.SendAsync("CONNECT example.com:443 HTTP/1.1\r\nHost: example.com:443\r\n\r\n");
        Assert.Contains("200 Connection Established", await client.ReadUntilAsync("\r\n\r\n", _wait), StringComparison.Ordinal);
        await client.SendAsync("ping");

        Assert.Contains("ping", await client.ReadUntilAsync("ping", _wait), StringComparison.Ordinal);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("8.8.8.8"), 443), Assert.Single(rig.Connector.Requested));
    }

    [Theory]
    [InlineData("CONNECT other.test:443 HTTP/1.1\r\n\r\n")]
    [InlineData("CONNECT example.com:22 HTTP/1.1\r\n\r\n")]
    [InlineData("CONNECT www.example.com:443 HTTP/1.1\r\n\r\n")]
    [InlineData("CONNECT example.com.evil.test:443 HTTP/1.1\r\n\r\n")]
    [InlineData("CONNECT 8.8.8.8:443 HTTP/1.1\r\n\r\n")]
    [InlineData("GET http://other.test/ HTTP/1.1\r\n\r\n")]
    public async Task Requests_outside_the_allowlist_get_403_and_touch_nothing(string request)
    {
        await using var rig = Build(true, ["example.com"], r => r.Answer("example.com", ["8.8.8.8"]).Answer("other.test", ["8.8.4.4"]));
        using var client = await RawClient.ConnectAsync(rig.Endpoint);

        await client.SendAsync(request);
        var (text, closed) = await client.ReadToEndAsync(_wait);

        Assert.StartsWith("HTTP/1.1 403", text, StringComparison.Ordinal);
        Assert.True(closed);
        Assert.Empty(rig.Connector.Requested);
        Assert.Equal(0, rig.Resolver.Calls); // a name that is not allowed is never even looked up
        Assert.Equal(0, rig.Upstream.Connections);
        var denied = Assert.Single(rig.Audit.OfKind(EgressAuditor.DenyKind));
        Assert.Contains("not-allowlisted", denied.Payload, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("169.254.169.254")]
    [InlineData("::1")]
    [InlineData("fd00:ec2::254")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("224.0.0.1")]
    public async Task A_dns_answer_pointing_inside_is_refused_after_resolution(string answer)
    {
        await using var rig = Build(true, ["example.com"], r => r.Answer("example.com", [answer]));
        using var client = await RawClient.ConnectAsync(rig.Endpoint);

        await client.SendAsync("CONNECT example.com:443 HTTP/1.1\r\n\r\n");
        var (text, _) = await client.ReadToEndAsync(_wait);

        Assert.StartsWith("HTTP/1.1 403", text, StringComparison.Ordinal);
        Assert.Empty(rig.Connector.Requested);
        Assert.Equal(0, rig.Upstream.Connections);
        Assert.Contains("forbidden-address", Assert.Single(rig.Audit.OfKind(EgressAuditor.DenyKind)).Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_forbidden_address_among_several_answers_refuses_the_name()
    {
        await using var rig = Build(true, ["example.com"], r => r.Answer("example.com", ["8.8.8.8", "10.0.0.5"]));
        using var client = await RawClient.ConnectAsync(rig.Endpoint);

        await client.SendAsync("CONNECT example.com:443 HTTP/1.1\r\n\r\n");
        var (text, _) = await client.ReadToEndAsync(_wait);

        Assert.StartsWith("HTTP/1.1 403", text, StringComparison.Ordinal);
        Assert.Empty(rig.Connector.Requested);
    }

    [Fact]
    public async Task A_listed_ip_literal_that_is_private_is_still_refused()
    {
        await using var rig = Build(true, ["127.0.0.1", "10.0.0.1"]);
        using var client = await RawClient.ConnectAsync(rig.Endpoint);

        await client.SendAsync("CONNECT 127.0.0.1:443 HTTP/1.1\r\n\r\n");
        var (text, _) = await client.ReadToEndAsync(_wait);

        Assert.StartsWith("HTTP/1.1 403", text, StringComparison.Ordinal);
        Assert.Empty(rig.Connector.Requested);
    }

    [Fact]
    public async Task A_listed_public_ip_literal_is_allowed_without_any_lookup()
    {
        await using var rig = Build(true, ["8.8.8.8"]);
        using var client = await RawClient.ConnectAsync(rig.Endpoint);

        await client.SendAsync("CONNECT 8.8.8.8:443 HTTP/1.1\r\n\r\n");

        Assert.Contains("200", await client.ReadUntilAsync("\r\n\r\n", _wait), StringComparison.Ordinal);
        Assert.Equal(0, rig.Resolver.Calls);
    }

    [Fact]
    public async Task Rebinding_cannot_move_a_connection_because_the_checked_address_is_pinned()
    {
        // First answer is public, every later answer is loopback. The proxy resolves once per request and connects to
        // exactly the address it checked, so the second answer can never be used for the first request.
        await using var rig = Build(true, ["example.com"], r => r.Answer("example.com", ["8.8.8.8"], ["127.0.0.1"]));

        using (var first = await RawClient.ConnectAsync(rig.Endpoint))
        {
            await first.SendAsync("CONNECT example.com:443 HTTP/1.1\r\n\r\n");
            Assert.Contains("200", await first.ReadUntilAsync("\r\n\r\n", _wait), StringComparison.Ordinal);
        }

        Assert.Equal(1, rig.Resolver.Calls);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("8.8.8.8"), 443), Assert.Single(rig.Connector.Requested));

        using var second = await RawClient.ConnectAsync(rig.Endpoint);
        await second.SendAsync("CONNECT example.com:443 HTTP/1.1\r\n\r\n");
        var (text, _) = await second.ReadToEndAsync(_wait);

        Assert.StartsWith("HTTP/1.1 403", text, StringComparison.Ordinal);
        Assert.Equal(2, rig.Resolver.Calls);
        Assert.Single(rig.Connector.Requested);
    }

    [Fact]
    public async Task Stop_closes_the_listener_and_an_open_tunnel_at_once()
    {
        await using var rig = Build(true, ["example.com"], r => r.Answer("example.com", ["8.8.8.8"]));
        using var client = await RawClient.ConnectAsync(rig.Endpoint);
        await client.SendAsync("CONNECT example.com:443 HTTP/1.1\r\n\r\n");
        await client.ReadUntilAsync("\r\n\r\n", _wait);
        await client.SendAsync("hello");
        await client.ReadUntilAsync("hello", _wait);
        var endpoint = rig.Endpoint;

        rig.Proxy.Stop();

        var (_, closed) = await client.ReadToEndAsync(_wait);
        Assert.True(closed);
        Assert.True(rig.Proxy.IsStopped);
        await Assert.ThrowsAnyAsync<SocketException>(async () =>
        {
            using var late = new TcpClient();
            await late.ConnectAsync(endpoint, TestContext.Current.CancellationToken);
        });
        await WaitForAsync(() => rig.Upstream.ClosedByPeer == 1);
        Assert.Equal(0, rig.Proxy.ActiveConnections);
    }

    [Fact]
    public async Task Stop_is_idempotent_and_a_stopped_proxy_cannot_restart()
    {
        await using var rig = Build(true, ["example.com"]);

        rig.Proxy.Stop();
        rig.Proxy.Stop();

        Assert.Throws<ObjectDisposedException>(() => rig.Proxy.Start());
    }

    [Fact]
    public async Task Allowlist_updates_apply_to_the_next_request()
    {
        await using var rig = Build(true, ["example.com"], r => r.Answer("example.com", ["8.8.8.8"]));

        rig.Proxy.UpdateAllowlist(Allowlist.Empty);
        using var client = await RawClient.ConnectAsync(rig.Endpoint);
        await client.SendAsync("CONNECT example.com:443 HTTP/1.1\r\n\r\n");
        var (text, _) = await client.ReadToEndAsync(_wait);

        Assert.StartsWith("HTTP/1.1 403", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("GET / HTTP/1.1\r\nHost: example.com\r\n\r\n")] // origin form, not a proxy request
    [InlineData("GET https://example.com/ HTTP/1.1\r\n\r\n")] // https must use CONNECT
    [InlineData("GET http://example.com@evil.test/ HTTP/1.1\r\n\r\n")]
    [InlineData("CONNECT example.com@evil.test:443 HTTP/1.1\r\n\r\n")]
    [InlineData("GET http://example.com/ HTTP/1.1\r\nContent-Length: 4\r\nTransfer-Encoding: chunked\r\n\r\n")]
    [InlineData("POST http://example.com/ HTTP/1.1\r\nContent-Length: 4\r\nContent-Length: 5\r\n\r\n")]
    [InlineData("GET http://example.com/ HTTP/1.1\r\nX: a\nb\r\n\r\n")]
    [InlineData("GET http://example.com/ HTTP/1.1\r\n folded: value\r\n\r\n")]
    [InlineData("GET http://example.com/ HTTP/1.1\r\nBad Header: x\r\n\r\n")]
    [InlineData("GET http://example.com/ HTTP/2.0\r\n\r\n")]
    [InlineData("GET  http://example.com/ HTTP/1.1\r\n\r\n")]
    [InlineData("NOT A REQUEST\r\n\r\n")]
    public async Task Malformed_or_ambiguous_requests_get_400_and_reach_nothing(string request)
    {
        await using var rig = Build(true, ["example.com"], r => r.Answer("example.com", ["8.8.8.8"]));
        using var client = await RawClient.ConnectAsync(rig.Endpoint);

        await client.SendAsync(request);
        var (text, closed) = await client.ReadToEndAsync(_wait);

        Assert.StartsWith("HTTP/1.1 400", text, StringComparison.Ordinal);
        Assert.True(closed);
        Assert.Empty(rig.Connector.Requested);
    }

    [Fact]
    public async Task An_oversized_head_gets_431()
    {
        await using var rig = Build(true, ["example.com"], options: new VmEgressOptions { MaxHeaderBytes = 1024 });
        using var client = await RawClient.ConnectAsync(rig.Endpoint);

        await client.SendAsync("GET http://example.com/ HTTP/1.1\r\nX-Big: " + new string('a', 4000) + "\r\n\r\n");
        var (text, _) = await client.ReadToEndAsync(_wait);

        Assert.StartsWith("HTTP/1.1 431", text, StringComparison.Ordinal);
        Assert.Empty(rig.Connector.Requested);
    }

    [Fact]
    public async Task Too_many_header_lines_get_431()
    {
        await using var rig = Build(true, ["example.com"], options: new VmEgressOptions { MaxHeaderCount = 5 });
        using var client = await RawClient.ConnectAsync(rig.Endpoint);

        var headers = string.Concat(Enumerable.Range(0, 20).Select(i => $"X-{i}: v\r\n"));
        await client.SendAsync("GET http://example.com/ HTTP/1.1\r\n" + headers + "\r\n");
        var (text, _) = await client.ReadToEndAsync(_wait);

        Assert.StartsWith("HTTP/1.1 431", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_client_that_never_finishes_its_head_is_dropped()
    {
        await using var rig = Build(true, ["example.com"], options: new VmEgressOptions { HeaderTimeout = TimeSpan.FromMilliseconds(300) });
        using var client = await RawClient.ConnectAsync(rig.Endpoint);

        await client.SendAsync("GET http://example.com/ HTT");
        var (_, closed) = await client.ReadToEndAsync(_wait);

        Assert.True(closed);
        await WaitForAsync(() => rig.Proxy.ActiveConnections == 0);
    }

    [Fact]
    public async Task An_idle_tunnel_is_closed()
    {
        await using var rig = Build(true, ["example.com"], r => r.Answer("example.com", ["8.8.8.8"]), new VmEgressOptions { IdleTimeout = TimeSpan.FromMilliseconds(400) });
        using var client = await RawClient.ConnectAsync(rig.Endpoint);
        await client.SendAsync("CONNECT example.com:443 HTTP/1.1\r\n\r\n");
        await client.ReadUntilAsync("\r\n\r\n", _wait);

        var (_, closed) = await client.ReadToEndAsync(_wait);

        Assert.True(closed);
        await WaitForAsync(() => rig.Upstream.ClosedByPeer == 1);
    }

    [Fact]
    public async Task Connections_over_the_cap_are_closed_at_once()
    {
        await using var rig = Build(true, ["example.com"], options: new VmEgressOptions { MaxConnectionsPerAgent = 2, HeaderTimeout = TimeSpan.FromSeconds(30) });
        using var a = await RawClient.ConnectAsync(rig.Endpoint);
        using var b = await RawClient.ConnectAsync(rig.Endpoint);
        await WaitForAsync(() => rig.Proxy.ActiveConnections == 2);

        var refused = false;
        try
        {
            using var c = await RawClient.ConnectAsync(rig.Endpoint);
            refused = (await c.ReadToEndAsync(_wait)).Closed;
        }
        catch (SocketException)
        {
            refused = true; // the reset can arrive before the connect call returns
        }

        Assert.True(refused);
        Assert.Equal(2, rig.Proxy.ActiveConnections);
    }

    [Fact]
    public async Task A_pipelined_second_request_in_the_same_write_never_reaches_upstream()
    {
        await using var rig = Build(false, ["example.com"], r => r.Answer("example.com", ["8.8.8.8"]));
        using var client = await RawClient.ConnectAsync(rig.Endpoint);

        await client.SendAsync("GET http://example.com/first HTTP/1.1\r\n\r\nGET http://example.com/second HTTP/1.1\r\nHost: other.test\r\n\r\n");
        var (text, closed) = await client.ReadToEndAsync(_wait);

        Assert.StartsWith("HTTP/1.1 400", text, StringComparison.Ordinal);
        Assert.True(closed);
        Assert.Empty(rig.Connector.Requested);
        Assert.Equal("", rig.Upstream.AllBytes);
        Assert.Contains("pipelined-request", Assert.Single(rig.Audit.OfKind(EgressAuditor.DenyKind)).Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bytes_sent_after_the_declared_body_are_dropped_and_the_connection_closes()
    {
        await using var rig = Build(false, ["example.com"], r => r.Answer("example.com", ["8.8.8.8"]));
        using var client = await RawClient.ConnectAsync(rig.Endpoint);

        await client.SendAsync("POST http://example.com/up HTTP/1.1\r\nContent-Length: 4\r\n\r\nabcd");
        await WaitForAsync(() => rig.Upstream.AllBytes.Contains("abcd", StringComparison.Ordinal));
        await client.SendAsync("GET http://example.com/second HTTP/1.1\r\nHost: other.test\r\n\r\n");
        var (_, closed) = await client.ReadToEndAsync(_wait);
        await Task.Delay(500, TestContext.Current.CancellationToken); // longer than the upstream's drain window

        Assert.True(closed);
        Assert.Contains("Content-Length: 4", rig.Upstream.AllBytes, StringComparison.Ordinal);
        Assert.DoesNotContain("second", rig.Upstream.AllBytes, StringComparison.Ordinal);
        Assert.DoesNotContain("other.test", rig.Upstream.AllBytes, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_body_with_content_length_is_forwarded_exactly()
    {
        await using var rig = Build(false, ["example.com"], r => r.Answer("example.com", ["8.8.8.8"]));
        using var client = await RawClient.ConnectAsync(rig.Endpoint);

        await client.SendAsync("POST http://example.com/up HTTP/1.1\r\nContent-Length: 10\r\n\r\n01234");
        await client.SendAsync("56789");
        await client.ReadToEndAsync(_wait);

        Assert.EndsWith("\r\n\r\n0123456789", rig.Upstream.AllBytes, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Chunked_requests_are_refused_because_their_end_is_not_parsed()
    {
        await using var rig = Build(false, ["example.com"], r => r.Answer("example.com", ["8.8.8.8"]));
        using var client = await RawClient.ConnectAsync(rig.Endpoint);

        await client.SendAsync("POST http://example.com/up HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n0\r\n\r\n");
        var (text, _) = await client.ReadToEndAsync(_wait);

        Assert.StartsWith("HTTP/1.1 501", text, StringComparison.Ordinal);
        Assert.Empty(rig.Connector.Requested);
    }

    [Fact]
    public async Task A_shrink_closes_open_tunnels_to_removed_destinations_and_keeps_the_others()
    {
        await using var rig = Build(true, ["a.example", "b.example"], r => r.Answer("a.example", ["8.8.8.8"]).Answer("b.example", ["8.8.4.4"]));
        using var a = await RawClient.ConnectAsync(rig.Endpoint);
        using var b = await RawClient.ConnectAsync(rig.Endpoint);
        await a.SendAsync("CONNECT a.example:443 HTTP/1.1\r\n\r\n");
        await a.ReadUntilAsync("\r\n\r\n", _wait);
        await b.SendAsync("CONNECT b.example:443 HTTP/1.1\r\n\r\n");
        await b.ReadUntilAsync("\r\n\r\n", _wait);

        Assert.True(Allowlist.TryCompile(["b.example"], out var shrunk, out _));
        rig.Proxy.UpdateAllowlist(shrunk);

        var (_, aClosed) = await a.ReadToEndAsync(_wait);
        Assert.True(aClosed);
        await b.SendAsync("still-here");
        Assert.Contains("still-here", await b.ReadUntilAsync("still-here", _wait), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("CONNECT user:pw@secret-token-host:443 HTTP/1.1\r\n\r\n", "secret-token-host")]
    [InlineData("GET /private?token=abc123 HTTP/1.1\r\nHost: x\r\n\r\n", "abc123")]
    [InlineData("GET http://user@evil.test/?token=abc123 HTTP/1.1\r\n\r\n", "abc123")]
    public async Task Deny_rows_never_carry_the_raw_request_target(string request, string secret)
    {
        await using var rig = Build(true, ["example.com"]);
        using var client = await RawClient.ConnectAsync(rig.Endpoint);

        await client.SendAsync(request);
        await client.ReadToEndAsync(_wait);

        var row = Assert.Single(rig.Audit.OfKind(EgressAuditor.DenyKind));
        Assert.DoesNotContain(secret, row.Payload, StringComparison.Ordinal);
        Assert.Contains("(unparsed)", row.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stopping_the_proxy_writes_the_held_back_deny_count()
    {
        await using var rig = Build(true, ["example.com"], options: new VmEgressOptions { AuditRowsPerWindow = 1 });
        for (var i = 0; i < 4; i++)
        {
            using var client = await RawClient.ConnectAsync(rig.Endpoint);
            await client.SendAsync("CONNECT other.test:443 HTTP/1.1\r\n\r\n");
            await client.ReadToEndAsync(_wait);
        }

        Assert.Empty(rig.Audit.OfKind(EgressAuditor.SuppressedKind));
        rig.Proxy.Stop();
        await rig.Proxy.DisposeAsync();

        var summary = Assert.Single(rig.Audit.OfKind(EgressAuditor.SuppressedKind));
        Assert.Contains("\"count\":3", summary.Payload, StringComparison.Ordinal);
    }

    private sealed class BadHeader(string name, string value) : IProxyRequestFilter
    {
        public ValueTask FilterAsync(ProxyRequestContext context, CancellationToken ct)
        {
            context.Headers.Add(new(name, value));
            return ValueTask.CompletedTask;
        }
    }

    [Theory]
    [InlineData("X-A", "v\r\nInjected: yes")]
    [InlineData("X-A", "v\nInjected: yes")]
    [InlineData("X-A", "v\0")]
    [InlineData("X-A\r\nInjected", "yes")]
    [InlineData("", "v")]
    [InlineData("X:A", "v")]
    public async Task A_filter_cannot_put_line_breaks_or_nul_into_the_head(string name, string value)
    {
        await using var rig = Build(false, ["example.com"], r => r.Answer("example.com", ["8.8.8.8"]), filters: [new BadHeader(name, value)]);
        using var client = await RawClient.ConnectAsync(rig.Endpoint);

        await client.SendAsync("GET http://example.com/ HTTP/1.1\r\n\r\n");
        var (text, _) = await client.ReadToEndAsync(_wait);

        Assert.StartsWith("HTTP/1.1 403", text, StringComparison.Ordinal);
        Assert.Equal("", rig.Upstream.AllBytes);
    }

    [Fact]
    public async Task A_filter_cannot_change_the_body_framing()
    {
        await using var rig = Build(false, ["example.com"], r => r.Answer("example.com", ["8.8.8.8"]), filters: [new BadHeader("Transfer-Encoding", "chunked")]);
        using var client = await RawClient.ConnectAsync(rig.Endpoint);

        await client.SendAsync("GET http://example.com/ HTTP/1.1\r\n\r\n");
        await client.ReadToEndAsync(_wait);

        Assert.DoesNotContain("Transfer-Encoding", rig.Upstream.AllBytes, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class HeaderAdder : IProxyRequestFilter
    {
        public ValueTask FilterAsync(ProxyRequestContext context, CancellationToken ct)
        {
            context.Headers.Add(new("X-Injected", context.Host + "|" + context.AgentId));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Rejecter : IProxyRequestFilter
    {
        public ValueTask FilterAsync(ProxyRequestContext context, CancellationToken ct)
        {
            context.Rejected = true;
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task The_request_filter_hook_can_change_headers_and_can_refuse()
    {
        await using (var rig = Build(false, ["example.com"], r => r.Answer("example.com", ["8.8.8.8"]), filters: [new HeaderAdder()]))
        {
            using var client = await RawClient.ConnectAsync(rig.Endpoint);
            await client.SendAsync("GET http://example.com/ HTTP/1.1\r\n\r\n");
            await client.ReadToEndAsync(_wait);

            Assert.Contains("X-Injected: example.com|agent-1", Assert.Single(rig.Upstream.Heads), StringComparison.Ordinal);
        }

        await using (var rig = Build(false, ["example.com"], r => r.Answer("example.com", ["8.8.8.8"]), filters: [new Rejecter()]))
        {
            using var client = await RawClient.ConnectAsync(rig.Endpoint);
            await client.SendAsync("GET http://example.com/ HTTP/1.1\r\n\r\n");
            var (text, _) = await client.ReadToEndAsync(_wait);

            Assert.StartsWith("HTTP/1.1 403", text, StringComparison.Ordinal);
            Assert.Empty(rig.Upstream.Heads);
        }
    }

    [Fact]
    public async Task Deny_rows_are_rate_limited_per_agent_and_summarised()
    {
        var time = new FakeTimeProvider();
        var audit = new CapturingAuditLog();
        var auditor = new EgressAuditor(audit, new VmEgressOptions { AuditRowsPerWindow = 3, AuditWindow = TimeSpan.FromMinutes(1) }, time);

        for (var i = 0; i < 10; i++)
        {
            await auditor.DenyAsync("a1", "evil.test", 443, "not-allowlisted", TestContext.Current.CancellationToken);
        }

        await auditor.DenyAsync("a2", "evil.test", 443, "not-allowlisted", TestContext.Current.CancellationToken);
        Assert.Equal(3 + 1, audit.OfKind(EgressAuditor.DenyKind).Count()); // 3 for a1, 1 for a2
        Assert.Empty(audit.OfKind(EgressAuditor.SuppressedKind));

        time.Advance(TimeSpan.FromMinutes(2));
        await auditor.DenyAsync("a1", "evil.test", 443, "not-allowlisted", TestContext.Current.CancellationToken);

        var summary = Assert.Single(audit.OfKind(EgressAuditor.SuppressedKind));
        Assert.Contains("\"count\":7", summary.Payload, StringComparison.Ordinal);
        Assert.Contains("\"agent\":\"a1\"", summary.Payload, StringComparison.Ordinal);
        Assert.Equal(3 + 1 + 1, audit.OfKind(EgressAuditor.DenyKind).Count());
    }

    [Fact]
    public async Task Audit_failures_do_not_break_the_proxy()
    {
        var auditor = new EgressAuditor(new ThrowingAuditLog(), new VmEgressOptions(), TimeProvider.System);

        await auditor.DenyAsync("a1", "evil.test", 443, "x", TestContext.Current.CancellationToken);
        await auditor.AllowAsync("a1", "example.com", 443, TestContext.Current.CancellationToken);
    }

    private sealed class ThrowingAuditLog : Bazaar.Server.Security.Audit.IAuditLog
    {
        public Task AppendAsync(Bazaar.Server.Security.Audit.AuditEntry e, CancellationToken ct = default) =>
            throw new InvalidOperationException("audit down");
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + _wait;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not reached in time");
            await Task.Delay(20);
        }
    }
}
