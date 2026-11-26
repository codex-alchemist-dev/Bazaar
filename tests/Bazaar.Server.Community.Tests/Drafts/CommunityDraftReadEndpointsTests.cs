using System.Net;
using System.Text.Json;
using Bazaar.Contracts.Community;
using Bazaar.Server.Community.Drafts;
using Bazaar.Server.Http;
using Bazaar.Server.Wire.Connection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Community.Tests.Drafts;

/// <summary>Drives <c>GET /community/drafts/{id}</c> and <c>GET /community/drafts</c> without a server.</summary>
public class CommunityDraftReadEndpointsTests
{
    private const string _token = "good-token";
    private static readonly Guid _ownerId = new("11111111-1111-1111-1111-111111111111");

    private sealed class FakeAuth : IConnectionAuthenticator
    {
        public string Kind { get; set; } = "owner";

        public ValueTask<AuthResult> AuthenticateAsync(HelloCredential? credential, DeviceInfo device, CancellationToken ct) =>
            ValueTask.FromResult(credential?.Value == _token ? AuthResult.Success(new PrincipalRef(_ownerId, Kind)) : AuthResult.Fail("bad"));
    }

    private static DefaultHttpContext Request(DraftFixture f, string? id, string query = "", string? token = _token, bool withServices = true, IPAddress? remote = null, string scheme = "https", string kind = "owner")
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new WireOptions()));
        services.AddSingleton<IConnectionAuthenticator>(new FakeAuth { Kind = kind });
        services.AddSingleton<TimeProvider>(f.Clock);
        services.AddSingleton(new HttpRateLimiter(new HttpSurfaceOptions { RequestBurst = 40, RequestsPerSecond = 1 }, TimeProvider.System));
        if (withServices)
        {
            services.AddSingleton<ICommunityDraftStore>(f.Store);
            services.AddSingleton(f.Handler);
        }

        var http = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        http.Request.Method = "GET";
        http.Request.Scheme = scheme;
        http.Request.QueryString = new QueryString(query);
        if (id is not null) http.Request.RouteValues["id"] = id;
        http.Connection.RemoteIpAddress = remote ?? IPAddress.Loopback;
        http.Response.Body = new MemoryStream();
        if (token is not null) http.Request.Headers.Authorization = "Bearer " + token;
        return http;
    }

    private static async Task<(int Status, string Body)> RunAsync(Func<HttpContext, Task<IResult>> route, DefaultHttpContext http)
    {
        var result = await route(http);
        await result.ExecuteAsync(http);
        http.Response.Body.Position = 0;
        return (http.Response.StatusCode, await new StreamReader(http.Response.Body).ReadToEndAsync());
    }

    [Fact]
    public async Task Get_returns_the_card_for_the_owner()
    {
        using var f = new DraftFixture();

        var (status, body) = await RunAsync(CommunityDraftReadEndpoints.GetAsync, Request(f, f.Draft.DraftId.ToString("D")));

        Assert.Equal(200, status);
        var card = JsonSerializer.Deserialize(body, CommunityContractsJsonContext.Default.DraftReviewCardDto)!;
        Assert.Equal(f.Draft.DraftId, card.DraftId);
        Assert.Equal(f.Draft.Post.Body, card.Body);
        Assert.Equal(f.Draft.Post.BodySha256, card.BodySha256);
        Assert.Equal(f.Draft.ApprovalId, card.Approval.ApprovalId);
    }

    [Fact]
    public async Task Get_guards_run_in_order_and_agents_are_refused()
    {
        using var f = new DraftFixture();
        var id = f.Draft.DraftId.ToString("D");

        Assert.Equal(403, (await RunAsync(CommunityDraftReadEndpoints.GetAsync, Request(f, id, token: null, remote: IPAddress.Parse("203.0.113.9"), scheme: "http"))).Status);
        Assert.Equal(401, (await RunAsync(CommunityDraftReadEndpoints.GetAsync, Request(f, id, token: null))).Status);
        Assert.Equal(401, (await RunAsync(CommunityDraftReadEndpoints.GetAsync, Request(f, id, token: "nope"))).Status);
        var agent = await RunAsync(CommunityDraftReadEndpoints.GetAsync, Request(f, id, kind: "agent"));
        Assert.Equal(403, agent.Status);
        Assert.DoesNotContain("Crash on start", agent.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_status_mapping_400_404_and_503()
    {
        using var f = new DraftFixture();

        Assert.Equal(400, (await RunAsync(CommunityDraftReadEndpoints.GetAsync, Request(f, "not-a-guid"))).Status);
        Assert.Equal(400, (await RunAsync(CommunityDraftReadEndpoints.GetAsync, Request(f, Guid.Empty.ToString("D")))).Status);
        Assert.Equal(404, (await RunAsync(CommunityDraftReadEndpoints.GetAsync, Request(f, Guid.NewGuid().ToString("D")))).Status);
        Assert.Equal(503, (await RunAsync(CommunityDraftReadEndpoints.GetAsync, Request(f, f.Draft.DraftId.ToString("D"), withServices: false))).Status);
    }

    [Fact]
    public async Task List_returns_open_pending_drafts_and_skips_expired_or_decided_ones()
    {
        using var f = new DraftFixture();
        var expired = f.NewDraft(gatesPass: true) with { ApprovalExpiresAt = DraftFixture.Start.AddMinutes(-1) };
        var denied = f.NewDraft(gatesPass: true) with { Status = CommunityDraftStatus.Denied };
        var second = f.NewDraft(gatesPass: true);
        f.Store.Add(expired);
        f.Store.Add(denied);
        f.Store.Add(second);

        var (status, body) = await RunAsync(CommunityDraftReadEndpoints.ListAsync, Request(f, null, "?status=pending"));

        Assert.Equal(200, status);
        var list = JsonSerializer.Deserialize(body, CommunityContractsJsonContext.Default.DraftReviewListDto)!;
        Assert.Equal([second.DraftId, f.Draft.DraftId], list.Drafts.Select(d => d.DraftId));
    }

    [Fact]
    public async Task List_defaults_to_pending_honours_limit_and_rejects_bad_query()
    {
        using var f = new DraftFixture();
        f.Store.Add(f.NewDraft(gatesPass: true));

        var one = await RunAsync(CommunityDraftReadEndpoints.ListAsync, Request(f, null, "?limit=1"));
        Assert.Equal(200, one.Status);
        Assert.Single(JsonSerializer.Deserialize(one.Body, CommunityContractsJsonContext.Default.DraftReviewListDto)!.Drafts);

        Assert.Equal(400, (await RunAsync(CommunityDraftReadEndpoints.ListAsync, Request(f, null, "?status=posted"))).Status);
        Assert.Equal(400, (await RunAsync(CommunityDraftReadEndpoints.ListAsync, Request(f, null, "?limit=0"))).Status);
        Assert.Equal(400, (await RunAsync(CommunityDraftReadEndpoints.ListAsync, Request(f, null, "?limit=101"))).Status);
        Assert.Equal(400, (await RunAsync(CommunityDraftReadEndpoints.ListAsync, Request(f, null, "?limit=abc"))).Status);
        Assert.Equal(401, (await RunAsync(CommunityDraftReadEndpoints.ListAsync, Request(f, null, token: null))).Status);
        Assert.Equal(503, (await RunAsync(CommunityDraftReadEndpoints.ListAsync, Request(f, null, withServices: false))).Status);
    }
}
