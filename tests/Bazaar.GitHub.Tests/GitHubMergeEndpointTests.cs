using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Bazaar.Approvals;
using Bazaar.Contracts.Git;
using Bazaar.GitHub.Adapter;
using Bazaar.GitHub.Auth;
using Bazaar.Server.Http;
using Bazaar.Server.Wire.Connection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bazaar.GitHub.Tests;

/// <summary><c>POST /github/merge</c> (doc 21, Merge route): caller checks, body checks and the result codes.</summary>
public sealed class GitHubMergeEndpointTests
{
    private const string _headSha = "a1b2c3d4e5f60718293a4b5c6d7e8f9012345678";
    private const string _deviceId = "device-1";
    private const long _installation = 4242;
    private static readonly Guid _owner = Guid.Parse("0196a6f0-0000-7000-8000-000000000701");
    private static readonly Guid _stranger = Guid.Parse("0196a6f0-0000-7000-8000-000000000702");

    private sealed class FakeAuthenticator : IConnectionAuthenticator
    {
        private readonly Dictionary<string, PrincipalRef> _known = [];

        public FakeAuthenticator Allow(string credential, PrincipalRef principal)
        {
            _known[credential] = principal;
            return this;
        }

        public ValueTask<AuthResult> AuthenticateAsync(HelloCredential? credential, DeviceInfo device, CancellationToken cancellationToken) =>
            ValueTask.FromResult(credential is not null && _known.TryGetValue(credential.Value, out var p)
                ? AuthResult.Success(p, null, null)
                : AuthResult.Fail("unknown credential"));
    }

    private sealed class RecordingMergeService : IOwnerSignedMergeService
    {
        public Exception? Throws { get; set; }

        public List<(OwnerMergeApproval Approval, MergeRequest Merge)> Calls { get; } = [];

        public Task MergeAsync(OwnerMergeApproval approval, MergeRequest merge, CancellationToken cancellationToken)
        {
            Calls.Add((approval, merge));
            return Throws is null ? Task.CompletedTask : Task.FromException(Throws);
        }
    }

    private sealed class SingleKeyRegistry(OwnerDeviceKey key) : IOwnerKeyRegistry
    {
        public Task<OwnerDeviceKey?> FindAsync(string deviceId, CancellationToken cancellationToken = default) =>
            Task.FromResult(deviceId == key.DeviceId ? key : null);
    }

    private static async Task<(WebApplication App, HttpClient Client)> StartAsync(IOwnerSignedMergeService service, long installation = _installation)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddOptions<WireOptions>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(new HttpSurfaceOptions());
        builder.Services.AddSingleton<HttpRateLimiter>();
        builder.Services.AddSingleton<IConnectionAuthenticator>(new FakeAuthenticator()
            .Allow("owner", new PrincipalRef(_owner, "human"))
            .Allow("stranger", new PrincipalRef(_stranger, "human"))
            .Allow("agent", new PrincipalRef(_owner, "agent")));
        builder.Services.AddSingleton(new GitHubMergeOptions { InstallationId = installation });
        builder.Services.AddSingleton(service);

        var app = builder.Build();
        app.MapBazaarGitHubRoutes();
        await app.StartAsync(TestContext.Current.CancellationToken);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return (app, new HttpClient { BaseAddress = new Uri(address) });
    }

    private static GitHubMergeRequestDto Body(Guid? human = null, Action<Dictionary<string, object?>>? tweak = null) =>
        new(
            new GitHubPrDto("codex-alchemist-dev", "bazaar", 42),
            new GitHubMergeDto(_headSha, "Add x (#42)", "Body"),
            new OwnerApprovalDto(
                OwnerMergeArgs.Action,
                Guid.Parse("0196a6f0-0000-7000-8000-000000000703"),
                new byte[32],
                _headSha,
                human ?? _owner,
                _deviceId,
                DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds(),
                DateTimeOffset.UtcNow.AddMinutes(3).ToUnixTimeMilliseconds(),
                Enumerable.Repeat((byte)3, OwnerSignedPayload.NonceLength).ToArray(),
                new byte[64]));

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string? bearer, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/github/merge") { Content = JsonContent.Create(body, options: new JsonSerializerOptions(JsonSerializerDefaults.Web)) };
        if (bearer is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static async Task<string> ErrorOf(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return doc.RootElement.GetProperty("error").GetString()!;
    }

    [Fact]
    public async Task The_owner_merges_and_the_server_chooses_the_installation()
    {
        var service = new RecordingMergeService();
        var (app, client) = await StartAsync(service);
        await using var _ = app;

        var response = await PostAsync(client, "owner", Body());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var call = Assert.Single(service.Calls);
        Assert.Equal(_installation, call.Approval.Pr.InstallationId);
        Assert.Equal(42, call.Approval.Pr.Number);
        Assert.Equal(_headSha, call.Merge.HeadSha);
        Assert.Equal(_deviceId, call.Approval.Payload.DeviceId);
        Assert.Equal(64, call.Approval.Signature.Length);
    }

    [Fact]
    public async Task A_request_without_a_bearer_is_unauthorized_and_reaches_no_merge()
    {
        var service = new RecordingMergeService();
        var (app, client) = await StartAsync(service);
        await using var _ = app;

        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(client, null, Body())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(client, "nobody", Body())).StatusCode);
        Assert.Empty(service.Calls);
    }

    [Theory]
    [InlineData("stranger")]
    [InlineData("agent")]
    public async Task Only_the_human_the_approval_names_may_present_it(string bearer)
    {
        var service = new RecordingMergeService();
        var (app, client) = await StartAsync(service);
        await using var _ = app;

        var response = await PostAsync(client, bearer, Body());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("forbidden", await ErrorOf(response));
        Assert.Empty(service.Calls);
    }

    [Fact]
    public async Task A_refused_approval_is_forbidden_with_its_reason()
    {
        var service = new RecordingMergeService { Throws = new MergeNotApprovedException("the PR head moved after approval, so the owner must approve again") };
        var (app, client) = await StartAsync(service);
        await using var _ = app;

        var response = await PostAsync(client, "owner", Body());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("merge_not_approved", await ErrorOf(response));
        Assert.Contains("approve again", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(typeof(GitHubApiException))]
    [InlineData(typeof(GitHubTokenException))]
    [InlineData(typeof(HttpRequestException))]
    public async Task A_github_or_token_failure_is_a_bad_gateway(Type exception)
    {
        var thrown = exception == typeof(GitHubApiException)
            ? new GitHubApiException(HttpStatusCode.InternalServerError, null, "boom")
            : exception == typeof(GitHubTokenException) ? (Exception)new GitHubTokenException("no token") : new HttpRequestException("down");
        var service = new RecordingMergeService { Throws = thrown };
        var (app, client) = await StartAsync(service);
        await using var _ = app;

        var response = await PostAsync(client, "owner", Body());

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("github_unavailable", await ErrorOf(response));
    }

    [Fact]
    public async Task Without_a_configured_installation_the_route_answers_bad_gateway_and_merges_nothing()
    {
        var service = new RecordingMergeService();
        var (app, client) = await StartAsync(service, installation: 0);
        await using var _ = app;

        var response = await PostAsync(client, "owner", Body());

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Empty(service.Calls);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("{\"pr\":{\"owner\":\"o\",\"repo\":\"r\",\"number\":0},\"merge\":{\"headSha\":\"x\",\"commitTitle\":\"t\",\"commitMessage\":\"m\"},\"approval\":{}}")]
    public async Task A_malformed_body_is_a_bad_request(string json)
    {
        var service = new RecordingMergeService();
        var (app, client) = await StartAsync(service);
        await using var _ = app;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/github/merge") { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "owner");

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(service.Calls);
    }

    [Fact]
    public async Task A_field_the_signed_layout_refuses_is_a_bad_request_not_a_forgery()
    {
        var service = new RecordingMergeService { Throws = new ArgumentException("A field contains a line break.") };
        var (app, client) = await StartAsync(service);
        await using var _ = app;

        var response = await PostAsync(client, "owner", Body());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_oversize_body_is_refused()
    {
        var service = new RecordingMergeService();
        var (app, client) = await StartAsync(service);
        await using var _ = app;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/github/merge") { Content = new StringContent(new string('a', 20_000), System.Text.Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "owner");

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.SendAsync(request, TestContext.Current.CancellationToken)).StatusCode);
        Assert.Empty(service.Calls);
    }

    [Fact]
    public async Task A_signed_merge_reaches_github_once_and_a_tampered_title_reaches_nothing()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var registry = new SingleKeyRegistry(new OwnerDeviceKey(_owner.ToString("D"), _deviceId, key.ExportSubjectPublicKeyInfo()));
        var verifier = new OwnerSignatureVerifier(registry, new InMemoryOwnerNonceStore());
        var http = new ScriptedHttpHandler();
        var adapter = new GitHubAdapter(
            new HttpClient(http),
            new StubTokenProvider("ghs_TESTONLY"),
            new InMemoryGitHubRateState(),
            new GitHubAdapterOptions { BaseAddress = new Uri("https://api.test/") },
            NullLogger<GitHubAdapter>.Instance);
        var commit = "/repos/codex-alchemist-dev/bazaar/commits/" + _headSha;
        http.Route(HttpMethod.Get, "/repos/codex-alchemist-dev/bazaar/pulls/42", Reply.Json(HttpStatusCode.OK, "{\"number\":42,\"head\":{\"sha\":\"" + _headSha + "\"}}"));
        http.Route(HttpMethod.Get, commit + "/check-runs?per_page=100", Reply.Json(HttpStatusCode.OK, "{\"total_count\":1,\"check_runs\":[{\"status\":\"completed\",\"conclusion\":\"success\"}]}"));
        http.Route(HttpMethod.Get, commit + "/status", Reply.Json(HttpStatusCode.OK, "{\"state\":\"pending\",\"total_count\":0,\"statuses\":[]}"));
        http.Route(HttpMethod.Put, "/repos/codex-alchemist-dev/bazaar/pulls/42/merge", Reply.Json(HttpStatusCode.OK, "{\"merged\":true}"));
        var (app, client) = await StartAsync(new OwnerSignedMerge(adapter, adapter, verifier));
        await using var _ = app;

        var pr = new PrRef(_installation, "codex-alchemist-dev", "bazaar", 42);
        var merge = new MergeRequest(_headSha, "Add x (#42)", "Body");
        var now = DateTimeOffset.UtcNow;
        var payload = new OwnerPayload(
            OwnerMergeArgs.Action, Guid.CreateVersion7(), OwnerMergeArgs.Hash(pr, merge), _headSha, _owner, _deviceId,
            now.AddMinutes(-1), now.AddMinutes(3), Enumerable.Repeat((byte)9, OwnerSignedPayload.NonceLength).ToArray());
        var signature = key.SignData(OwnerSignedPayload.Build(payload), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var dto = new GitHubMergeRequestDto(
            new GitHubPrDto(pr.Owner, pr.Repo, pr.Number),
            new GitHubMergeDto(merge.HeadSha, merge.CommitTitle, merge.CommitMessage),
            new OwnerApprovalDto(payload.Action, payload.ApprovalId, payload.ArgsHash, payload.HeadSha, payload.HumanId, payload.DeviceId,
                payload.IssuedAt.ToUnixTimeMilliseconds(), payload.ExpiresAt.ToUnixTimeMilliseconds(), payload.Nonce, signature));

        var tampered = dto with { Merge = dto.Merge with { CommitTitle = "Something the owner never saw" } };
        var refused = await PostAsync(client, "owner", tampered);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.DoesNotContain(http.Requests, r => r.Method == "PUT");

        var merged = await PostAsync(client, "owner", dto);
        Assert.Equal(HttpStatusCode.OK, merged.StatusCode);
        Assert.Single(http.Requests, r => r.Method == "PUT");

        // The same signed request cannot be replayed: its nonce is spent.
        var replay = await PostAsync(client, "owner", dto);
        Assert.Equal(HttpStatusCode.Forbidden, replay.StatusCode);
        Assert.Single(http.Requests, r => r.Method == "PUT");
    }
}
