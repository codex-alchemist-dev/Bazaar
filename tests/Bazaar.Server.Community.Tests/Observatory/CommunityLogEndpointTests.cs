using System.Net;
using System.Text.Json;
using Bazaar.Server.Community.Actions;
using Bazaar.Server.Community.Observatory;
using Bazaar.Contracts.Community;
using Bazaar.Server.Http;
using Bazaar.Server.Wire.Connection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Community.Tests.Observatory;

/// <summary>Drives <c>GET /community/log</c> without a server: auth order, owner-only rule, parameters and the response shape.</summary>
public class CommunityLogEndpointTests
{
    private const string _token = "good-token";
    private static readonly Guid _ownerId = new("11111111-1111-1111-1111-111111111111");

    private sealed class FakeAuth : IConnectionAuthenticator
    {
        public string Kind { get; set; } = "owner";

        public ValueTask<AuthResult> AuthenticateAsync(HelloCredential? credential, DeviceInfo device, CancellationToken ct) =>
            ValueTask.FromResult(credential?.Value == _token
                ? AuthResult.Success(new PrincipalRef(_ownerId, Kind))
                : AuthResult.Fail("bad"));
    }

    private sealed class FakeReader : ICommunityLogReader
    {
        public CommunityLogFilter? Seen { get; private set; }

        public CommunityLogPage Page { get; set; } = new([], null);

        public Task<CommunityLogPage> ListPageAsync(CommunityLogFilter filter, CancellationToken ct = default)
        {
            Seen = filter;
            return Task.FromResult(Page);
        }
    }

    private static (DefaultHttpContext Http, FakeReader Reader, FakeAuth Auth) Request(
        string query, string? token = _token, IPAddress? remote = null, bool withReader = true, int burst = 40)
    {
        var reader = new FakeReader();
        var auth = new FakeAuth();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new WireOptions()));
        services.AddSingleton<IConnectionAuthenticator>(auth);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new HttpRateLimiter(new HttpSurfaceOptions { RequestBurst = burst, RequestsPerSecond = 1 }, TimeProvider.System));
        if (withReader) services.AddSingleton<ICommunityLogReader>(reader);

        var http = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        http.Request.QueryString = new QueryString(query);
        http.Request.Scheme = "https";
        http.Connection.RemoteIpAddress = remote ?? IPAddress.Loopback;
        http.Response.Body = new MemoryStream();
        if (token is not null) http.Request.Headers.Authorization = "Bearer " + token;
        return (http, reader, auth);
    }

    private static async Task<(int Status, string Body)> RunAsync(DefaultHttpContext http)
    {
        var result = await CommunityLogEndpoint.HandleAsync(http);
        await result.ExecuteAsync(http);
        http.Response.Body.Position = 0;
        return (http.Response.StatusCode, await new StreamReader(http.Response.Body).ReadToEndAsync());
    }

    private static CommunityLogRow Row(long id, DateTimeOffset at) =>
        new(id, "agent-a", "github", CommunityAction.NewPost, "https://example.test/x", new string('a', 64), "done", at, null, null);

    [Fact]
    public async Task Missing_or_wrong_token_is_401()
    {
        var (none, _, _) = Request("", token: null);
        var (bad, _, _) = Request("", token: "nope");

        Assert.Equal(401, (await RunAsync(none)).Status);
        Assert.Equal(401, (await RunAsync(bad)).Status);
    }

    [Fact]
    public async Task Non_owner_is_403_and_the_reader_is_not_called()
    {
        var (http, reader, auth) = Request("");
        auth.Kind = "agent";

        var (status, body) = await RunAsync(http);

        Assert.Equal(403, status);
        Assert.Null(reader.Seen);
        Assert.Contains("forbidden", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plain_http_from_a_remote_address_is_403()
    {
        var (http, _, _) = Request("", remote: IPAddress.Parse("203.0.113.9"));
        http.Request.Scheme = "http";

        Assert.Equal(403, (await RunAsync(http)).Status);
    }

    [Fact]
    public async Task Rate_limit_is_429()
    {
        var (http, _, _) = Request("", burst: 1);
        var services = http.RequestServices;
        Assert.Equal(200, (await RunAsync(http)).Status);

        var second = new DefaultHttpContext { RequestServices = services };
        second.Request.Headers.Authorization = "Bearer " + _token;
        second.Request.Scheme = "https";
        second.Connection.RemoteIpAddress = IPAddress.Loopback;
        second.Response.Body = new MemoryStream();

        Assert.Equal(429, (await RunAsync(second)).Status);
    }

    [Fact]
    public async Task Parameters_reach_the_reader_and_the_limit_is_clamped()
    {
        var (http, reader, _) = Request("?agentId=agent-a&platform=github&limit=9999");

        Assert.Equal(200, (await RunAsync(http)).Status);

        Assert.Equal("agent-a", reader.Seen!.AgentId);
        Assert.Equal("github", reader.Seen.Platform);
        Assert.Equal(CommunityLogFilter.MaxLimit, reader.Seen.Limit);
        Assert.Null(reader.Seen.After);
    }

    [Theory]
    [InlineData("?limit=0", 1)]
    [InlineData("?limit=-4", 1)]
    [InlineData("?limit=7", 7)]
    [InlineData("", 50)]
    [InlineData("?agentId=&platform=", 50)]
    public async Task Limit_defaults_and_empty_filters_mean_all(string query, int expected)
    {
        var (http, reader, _) = Request(query);

        Assert.Equal(200, (await RunAsync(http)).Status);

        Assert.Equal(expected, reader.Seen!.Limit);
        Assert.Null(reader.Seen.AgentId);
        Assert.Null(reader.Seen.Platform);
    }

    [Theory]
    [InlineData("?limit=abc")]
    [InlineData("?limit=1.5")]
    [InlineData("?limit=99999999999999999999")]
    public async Task Bad_limit_is_400(string query)
    {
        var (http, reader, _) = Request(query);

        Assert.Equal(400, (await RunAsync(http)).Status);
        Assert.Null(reader.Seen);
    }

    [Theory]
    [InlineData("?cursor=garbage!!")]
    [InlineData("?cursor=AAAA")]
    [InlineData("?cursor=djEuMS4x==")]
    public async Task Malformed_cursor_is_400_with_a_short_code_and_no_trace(string query)
    {
        var (http, reader, _) = Request(query);

        var (status, body) = await RunAsync(http);

        Assert.Equal(400, status);
        Assert.Null(reader.Seen);
        Assert.Contains("bad_cursor", body, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Page_is_returned_with_an_encoded_next_cursor_that_decodes_back()
    {
        var at = new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);
        var (http, reader, _) = Request("");
        reader.Page = new CommunityLogPage([Row(7, at), Row(6, at)], new CommunityLogPosition(at, 6));

        var (status, body) = await RunAsync(http);

        Assert.Equal(200, status);
        var page = JsonSerializer.Deserialize(body, CommunityContractsJsonContext.Default.CommunityLogPageDto)!;
        Assert.Equal([7L, 6L], page.Rows.Select(r => r.Id).ToArray());
        Assert.Equal("NewPost", page.Rows[0].Action);
        Assert.True(CommunityLogCursor.TryDecode(page.NextCursor, out var next));
        Assert.Equal(new CommunityLogPosition(at, 6), next);
    }

    [Fact]
    public async Task Cursor_from_the_previous_page_becomes_the_after_position()
    {
        var position = new CommunityLogPosition(new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero), 6);
        var (http, reader, _) = Request("?cursor=" + CommunityLogCursor.Encode(position));

        Assert.Equal(200, (await RunAsync(http)).Status);

        Assert.Equal(position, reader.Seen!.After);
    }

    [Fact]
    public async Task Last_page_has_no_next_cursor_and_the_body_has_no_body_text_field()
    {
        var (http, reader, _) = Request("");
        reader.Page = new CommunityLogPage([Row(1, DateTimeOffset.UnixEpoch)], null);

        var (_, body) = await RunAsync(http);

        using var doc = JsonDocument.Parse(body);
        Assert.False(doc.RootElement.TryGetProperty("nextCursor", out _));
        var row = doc.RootElement.GetProperty("rows")[0];
        Assert.False(row.TryGetProperty("body", out _));
        Assert.True(row.TryGetProperty("bodySha256", out _));
    }

    [Fact]
    public async Task No_registered_reader_is_503()
    {
        var (http, _, _) = Request("", withReader: false);

        Assert.Equal(503, (await RunAsync(http)).Status);
    }
}
