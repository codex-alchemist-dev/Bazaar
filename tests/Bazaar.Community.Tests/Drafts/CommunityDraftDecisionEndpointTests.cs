using System.Net;
using System.Text;
using System.Text.Json;
using Bazaar.Community.Actions;
using Bazaar.Community.Drafts;
using Bazaar.Contracts.Approvals;
using Bazaar.Contracts.Community;
using Bazaar.Server.Http;
using Bazaar.Server.Wire.Connection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Bazaar.Community.Tests.Drafts;

/// <summary>Drives <c>POST /community/drafts/{id}/decision</c> without a server: guard order, body handling and the status mapping.</summary>
public class CommunityDraftDecisionEndpointTests
{
    private const string _token = "good-token";
    private static readonly Guid _ownerId = new("11111111-1111-1111-1111-111111111111");

    private sealed class FakeAuth : IConnectionAuthenticator
    {
        public string Kind { get; set; } = "owner";

        public ValueTask<AuthResult> AuthenticateAsync(HelloCredential? credential, DeviceInfo device, CancellationToken ct) =>
            ValueTask.FromResult(credential?.Value == _token ? AuthResult.Success(new PrincipalRef(_ownerId, Kind)) : AuthResult.Fail("bad"));
    }

    private static DefaultHttpContext Request(DraftFixture f, Guid id, string? json, string? token = _token, bool withHandler = true, int burst = 40, IPAddress? remote = null, string scheme = "https")
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new WireOptions()));
        services.AddSingleton<IConnectionAuthenticator>(new FakeAuth());
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new HttpRateLimiter(new HttpSurfaceOptions { RequestBurst = burst, RequestsPerSecond = 1 }, TimeProvider.System));
        if (withHandler) services.AddSingleton(f.Handler);

        var http = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        http.Request.Method = "POST";
        http.Request.Scheme = scheme;
        http.Request.RouteValues["id"] = id.ToString("D");
        http.Connection.RemoteIpAddress = remote ?? IPAddress.Loopback;
        http.Response.Body = new MemoryStream();
        if (token is not null) http.Request.Headers.Authorization = "Bearer " + token;
        if (json is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            http.Request.Body = new MemoryStream(bytes);
            http.Request.ContentLength = bytes.Length;
        }

        return http;
    }

    private static string Json(DraftReviewDecisionDto dto) =>
        JsonSerializer.Serialize(dto, CommunityContractsJsonContext.Default.DraftReviewDecisionDto);

    private static async Task<(int Status, string Body)> RunAsync(DefaultHttpContext http)
    {
        var result = await CommunityDraftDecisionEndpoint.HandleAsync(http);
        await result.ExecuteAsync(http);
        http.Response.Body.Position = 0;
        return (http.Response.StatusCode, await new StreamReader(http.Response.Body).ReadToEndAsync());
    }

    [Fact]
    public async Task Guards_run_in_order_plaintext_403_then_401_then_owner_403()
    {
        using var f = new DraftFixture();
        var json = Json(f.Sign(f.Draft, ApprovalDecisionKind.ApproveOnce));

        Assert.Equal(403, (await RunAsync(Request(f, f.Draft.DraftId, json, token: null, remote: IPAddress.Parse("203.0.113.9"), scheme: "http"))).Status);
        Assert.Equal(401, (await RunAsync(Request(f, f.Draft.DraftId, json, token: null))).Status);
        Assert.Equal(401, (await RunAsync(Request(f, f.Draft.DraftId, json, token: "nope"))).Status);

        var agent = Request(f, f.Draft.DraftId, json);
        ((FakeAuth)agent.RequestServices.GetRequiredService<IConnectionAuthenticator>()).Kind = "agent";
        Assert.Equal(403, (await RunAsync(agent)).Status);
        Assert.Empty(f.Adapter.Posts);
    }

    [Fact]
    public async Task Rate_limit_is_429()
    {
        using var f = new DraftFixture();
        var first = Request(f, f.Draft.DraftId, "{}", burst: 1);
        Assert.Equal(400, (await RunAsync(first)).Status);

        var second = Request(f, f.Draft.DraftId, "{}");
        var shared = first.RequestServices;
        second.RequestServices = shared;

        Assert.Equal(429, (await RunAsync(second)).Status);
    }

    [Fact]
    public async Task Approve_returns_200_with_the_receipt_and_replay_is_200_again()
    {
        using var f = new DraftFixture();
        var json = Json(f.Sign(f.Draft, ApprovalDecisionKind.ApproveOnce));

        var (status, body) = await RunAsync(Request(f, f.Draft.DraftId, json));
        var (replayStatus, replayBody) = await RunAsync(Request(f, f.Draft.DraftId, json));

        Assert.Equal(200, status);
        var dto = JsonSerializer.Deserialize(body, CommunityContractsJsonContext.Default.CommunityDraftDecisionResultDto)!;
        Assert.Equal("posted", dto.Outcome);
        Assert.Equal("https://example.test/issues/42", dto.Url);
        Assert.Equal(200, replayStatus);
        Assert.True(JsonSerializer.Deserialize(replayBody, CommunityContractsJsonContext.Default.CommunityDraftDecisionResultDto)!.AlreadyDecided);
        Assert.Single(f.Adapter.Posts);
        Assert.DoesNotContain("Crash on start", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_mapping_400_403_404_409_and_503()
    {
        using var f = new DraftFixture();
        var good = f.Sign(f.Draft, ApprovalDecisionKind.ApproveOnce);

        Assert.Equal(400, (await RunAsync(Request(f, f.Draft.DraftId, "{not json"))).Status);
        Assert.Equal(400, (await RunAsync(Request(f, f.Draft.DraftId, null))).Status);
        Assert.Equal(400, (await RunAsync(Request(f, Guid.Empty, Json(good)))).Status);
        Assert.Equal(404, (await RunAsync(Request(f, Guid.Parse("22222222-2222-2222-2222-222222222222"), Json(good with { DraftId = Guid.Parse("22222222-2222-2222-2222-222222222222") })))).Status);
        Assert.Equal(403, (await RunAsync(Request(f, f.Draft.DraftId, Json(f.Sign(f.Draft, ApprovalDecisionKind.ApproveOnce, deviceId: "ghost"))))).Status);
        Assert.Equal(503, (await RunAsync(Request(f, f.Draft.DraftId, Json(good), withHandler: false))).Status);

        f.Clock.Now = f.Draft.ApprovalExpiresAt;
        Assert.Equal(409, (await RunAsync(Request(f, f.Draft.DraftId, Json(good)))).Status);
    }

    [Fact]
    public async Task Oversized_body_is_413_before_the_handler_runs()
    {
        using var f = new DraftFixture();
        var big = new string('a', CommunityDraftDecisionEndpoint.MaxBodyBytes + 1);
        var http = Request(f, f.Draft.DraftId, big);

        Assert.Equal(413, (await RunAsync(http)).Status);

        var chunked = Request(f, f.Draft.DraftId, big);
        chunked.Request.ContentLength = null;
        Assert.Equal(413, (await RunAsync(chunked)).Status);
        Assert.Empty(f.Adapter.Posts);
    }

    [Fact]
    public async Task Edit_over_http_returns_the_new_card_and_does_not_post()
    {
        using var f = new DraftFixture();
        var json = Json(f.Sign(f.Draft, ApprovalDecisionKind.EditThenApprove, edited: "Shorter."));

        var (status, body) = await RunAsync(Request(f, f.Draft.DraftId, json));

        Assert.Equal(200, status);
        var dto = JsonSerializer.Deserialize(body, CommunityContractsJsonContext.Default.CommunityDraftDecisionResultDto)!;
        Assert.Equal("edited", dto.Outcome);
        Assert.StartsWith("Shorter.", dto.NewDraft!.Body, StringComparison.Ordinal);
        Assert.Empty(f.Adapter.Posts);
    }

    [Fact]
    public void Route_template_matches_the_shared_contract()
    {
        Assert.Equal("community/drafts/11111111-1111-1111-1111-111111111111/decision", CommunityRoutes.DraftDecision(Guid.Parse("11111111-1111-1111-1111-111111111111")));
    }
}
