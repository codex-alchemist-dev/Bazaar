using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Bazaar.Approvals;
using Bazaar.Contracts.Approvals;
using Bazaar.Server.Security.Approvals;
using Bazaar.Server.Security.Policy;
using Bazaar.Server.Wire.Connection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Approvals.Http.Tests;

/// <summary>The phone's approval routes over the real approval service, with the in-memory store and a real P-256 key.</summary>
public sealed class ApprovalRouteTests : IAsyncLifetime
{
    private const string _deviceId = "device-1";
    private static readonly DateTimeOffset _now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeAuth : IConnectionAuthenticator
    {
        public ValueTask<AuthResult> AuthenticateAsync(HelloCredential? credential, DeviceInfo device, CancellationToken cancellationToken) =>
            ValueTask.FromResult(credential?.Value switch
            {
                "owner" => AuthResult.Success(new PrincipalRef(Guid.NewGuid(), "owner")),
                "human" => AuthResult.Success(new PrincipalRef(Guid.NewGuid(), "human")),
                _ => AuthResult.Fail("no"),
            });
    }

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly InMemoryApprovalStore _store = new();
    private readonly FakeTimeProvider _time = new(_now);
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        var keys = new InMemoryOwnerKeyRegistry();
        keys.Register(new OwnerDeviceKey("owner", _deviceId, _key.ExportSubjectPublicKeyInfo()));
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddRouting();
        builder.Services.AddSingleton<TimeProvider>(_time);
        builder.Services.AddSingleton<IConnectionAuthenticator>(new FakeAuth());
        builder.Services.AddSingleton(_store);
        builder.Services.AddSingleton<IPendingApprovalLister>(_store);
        builder.Services.AddSingleton(new ApprovalService(_store, keys, new InMemoryRateBucketStore(), new ApprovalOptions()));
        _app = builder.Build();
        _app.MapBazaarApprovalRoutes();
        await _app.StartAsync(Ct);
        _client = _app.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        _key.Dispose();
        await _app.DisposeAsync();
    }

    private async Task<ApprovalRequest> AddAsync(TimeSpan? expiresIn = null, DateTimeOffset? requestedAt = null)
    {
        var request = new ApprovalRequest(
            Guid.NewGuid(), "charles", Guid.Empty, "pr.merge", "repo#12", RandomNumberGenerator.GetBytes(32), "{\"pr\":12}",
            ImmutableArray<Bazaar.Server.Security.Taint.TaintSource>.Empty, ApprovalStatus.Pending,
            requestedAt ?? _now, _now + (expiresIn ?? TimeSpan.FromHours(1)));
        await _store.InsertAsync(request, Ct);
        return request;
    }

    private ApprovalDecisionDto Decision(ApprovalRequest r, ApprovalDecision decision = ApprovalDecision.Approve, bool badSignature = false)
    {
        var signature = _key.SignData(ApprovalSignature.Message(r.Id, decision, r.ArgsHash, r.ExpiresAt, _deviceId), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        if (badSignature)
        {
            signature[0] ^= 0xFF;
        }

        return new ApprovalDecisionDto(
            r.Id,
            decision == ApprovalDecision.Approve ? ApprovalDecisionKind.ApproveOnce : ApprovalDecisionKind.Deny,
            _deviceId, Convert.ToBase64String(signature), _now);
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? token, ApprovalDecisionDto? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, ApprovalContractsJsonContext.Default.ApprovalDecisionDto);
        }

        return _client.SendAsync(request, Ct);
    }

    [Fact]
    public async Task Pending_lists_unexpired_pending_cards_newest_first_for_the_owner()
    {
        var older = await AddAsync(requestedAt: _now.AddMinutes(-10));
        var newer = await AddAsync(requestedAt: _now.AddMinutes(-1));
        await AddAsync(expiresIn: TimeSpan.FromSeconds(-1));

        var response = await SendAsync(HttpMethod.Get, "/" + ApprovalRoutes.Pending, "owner");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cards = await response.Content.ReadFromJsonAsync(ApprovalContractsJsonContext.Default.ApprovalCardDtoArray, Ct);
        Assert.Equal([newer.Id, older.Id], cards!.Select(c => c.ApprovalId));
        Assert.All(cards!, c => Assert.Equal(ApprovalCardStatus.Pending, c.Status));
    }

    [Fact]
    public async Task Both_routes_need_a_signed_in_owner()
    {
        var request = await AddAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/" + ApprovalRoutes.Pending, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/" + ApprovalRoutes.Pending, "bogus")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Get, "/" + ApprovalRoutes.Pending, "human")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(HttpMethod.Post, "/" + ApprovalRoutes.Decision(request.Id), "human", Decision(request))).StatusCode);
        Assert.Equal(ApprovalStatus.Pending, (await _store.GetAsync(request.Id, Ct))!.Status);
    }

    [Fact]
    public async Task A_signed_approve_is_recorded_and_the_approval_leaves_the_pending_list()
    {
        var request = await AddAsync();

        var response = await SendAsync(HttpMethod.Post, "/" + ApprovalRoutes.Decision(request.Id), "owner", Decision(request));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(ApprovalStatus.Approved, (await _store.GetAsync(request.Id, Ct))!.Status);
        Assert.Empty(await _store.ListPendingAsync(_now, 10, Ct));
    }

    [Fact]
    public async Task A_signed_deny_is_recorded()
    {
        var request = await AddAsync();

        var response = await SendAsync(HttpMethod.Post, "/" + ApprovalRoutes.Decision(request.Id), "owner", Decision(request, ApprovalDecision.Deny));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(ApprovalStatus.Denied, (await _store.GetAsync(request.Id, Ct))!.Status);
    }

    [Fact]
    public async Task A_bad_signature_is_refused_and_changes_nothing()
    {
        var request = await AddAsync();

        var response = await SendAsync(HttpMethod.Post, "/" + ApprovalRoutes.Decision(request.Id), "owner", Decision(request, badSignature: true));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(ApprovalStatus.Pending, (await _store.GetAsync(request.Id, Ct))!.Status);
    }

    [Fact]
    public async Task A_decision_with_no_signature_is_a_bad_request()
    {
        var request = await AddAsync();
        var unsigned = Decision(request) with { SignatureBase64 = null };

        var response = await SendAsync(HttpMethod.Post, "/" + ApprovalRoutes.Decision(request.Id), "owner", unsigned);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApprovalStatus.Pending, (await _store.GetAsync(request.Id, Ct))!.Status);
    }

    [Fact]
    public async Task The_body_id_must_match_the_path_id()
    {
        var request = await AddAsync();
        var other = await AddAsync();

        var response = await SendAsync(HttpMethod.Post, "/" + ApprovalRoutes.Decision(other.Id), "owner", Decision(request));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_expired_and_opposite_decisions_get_their_own_status()
    {
        var unknown = new ApprovalRequest(Guid.NewGuid(), "charles", Guid.Empty, "t", null, RandomNumberGenerator.GetBytes(32), "{}", [], ApprovalStatus.Pending, _now, _now.AddHours(1));
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Post, "/" + ApprovalRoutes.Decision(unknown.Id), "owner", Decision(unknown))).StatusCode);

        var expiring = await AddAsync(expiresIn: TimeSpan.FromMinutes(5));
        var signed = Decision(expiring);
        _time.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(HttpStatusCode.Gone, (await SendAsync(HttpMethod.Post, "/" + ApprovalRoutes.Decision(expiring.Id), "owner", signed)).StatusCode);

        var request = await AddAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Post, "/" + ApprovalRoutes.Decision(request.Id), "owner", Decision(request))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Post, "/" + ApprovalRoutes.Decision(request.Id), "owner", Decision(request))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SendAsync(HttpMethod.Post, "/" + ApprovalRoutes.Decision(request.Id), "owner", Decision(request, ApprovalDecision.Deny))).StatusCode);
    }
}
