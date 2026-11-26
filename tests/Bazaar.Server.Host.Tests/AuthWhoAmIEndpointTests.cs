using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bazaar.Server.Components.Remote;
using Bazaar.Server.Host.Auth;
using Bazaar.Server.Http.Auth;
using Bazaar.Server.Identity.Signing;
using Bazaar.Server.Wire.Connection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Host.Tests;

/// <summary><c>GET /auth/whoami</c> (doc 32 section 5): one 401 for every bad token, 403 for plaintext off the machine, 503 without a verifier.</summary>
public sealed class AuthWhoAmIEndpointTests
{
    private static readonly DateTimeOffset _now = DateTimeOffset.UtcNow;
    private const string _invalidToken = """{"error":"invalid_token"}""";

    private sealed class Harness
    {
        public required SignerAccessTokenIssuer Issuer { get; init; }
        public required InMemoryRefreshCredentialStore Store { get; init; }
        public required ServiceProvider Services { get; init; }
        public required Guid DeviceId { get; init; }
        public required Guid HumanId { get; init; }

        public string Token(DateTimeOffset? expiresAt = null) => Issuer.Issue(DeviceId, HumanId, expiresAt ?? _now.AddMinutes(10));
    }

    private static AccessTokenSigner NewSigner() =>
        new AccessTokenSigner(new InMemorySigningKeyStore(), new SigningKeyKekWrapper(new TestKek()), TimeProvider.System);

    private static async Task<Harness> CreateAsync(string status = "active", bool withVerifier = true)
    {
        var signer = NewSigner();
        var store = new InMemoryRefreshCredentialStore();
        var humanId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        store.SetHuman(new HumanRecord(humanId, "Mira Okafor", "coworker", status));
        await store.CreateDeviceAsync(
            new DeviceRecord(deviceId, humanId, "laptop", "windows", 1, null),
            Guid.NewGuid(), RandomNumberGenerator.GetBytes(32), _now, _now.AddDays(30), TestContext.Current.CancellationToken);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new WireOptions()));
        services.AddSingleton<IRefreshCredentialStore>(store);
        services.AddSingleton<SignedInPersonLookup>();
        if (withVerifier) services.AddSingleton<IAccessTokenVerifier>(new SignerAccessTokenVerifier(signer));
        return new Harness
        {
            Issuer = new SignerAccessTokenIssuer(signer),
            Store = store,
            Services = services.BuildServiceProvider(),
            DeviceId = deviceId,
            HumanId = humanId,
        };
    }

    private static async Task<(int Status, string Body)> GetAsync(Harness h, string? authorization, IPAddress? remote = null, bool https = false)
    {
        var context = new DefaultHttpContext { RequestServices = h.Services };
        context.Request.Method = "GET";
        context.Request.Path = "/auth/whoami";
        context.Request.IsHttps = https;
        context.Connection.RemoteIpAddress = remote ?? IPAddress.Loopback;
        if (authorization is not null) context.Request.Headers.Authorization = authorization;
        context.Response.Body = new MemoryStream();

        var result = await AuthWhoAmIEndpoint.HandleAsync(context);
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        return (context.Response.StatusCode, Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray()));
    }

    [Fact]
    public async Task Valid_token_returns_the_human_and_device()
    {
        var h = await CreateAsync();

        var (status, body) = await GetAsync(h, "Bearer " + h.Token());

        Assert.Equal(200, status);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(h.HumanId, doc.RootElement.GetProperty("human_id").GetGuid());
        Assert.Equal("Mira Okafor", doc.RootElement.GetProperty("display_name").GetString());
        Assert.Equal("coworker", doc.RootElement.GetProperty("role").GetString());
        Assert.Equal(h.DeviceId, doc.RootElement.GetProperty("device_id").GetGuid());
    }

    [Fact]
    public async Task Expired_token_is_refused()
    {
        var h = await CreateAsync();
        Assert.Equal((401, _invalidToken), await GetAsync(h, "Bearer " + h.Token(_now.AddMinutes(-1))));
    }

    [Fact]
    public async Task Token_with_a_bad_signature_is_refused()
    {
        var h = await CreateAsync();
        var foreign = new SignerAccessTokenIssuer(NewSigner()).Issue(h.DeviceId, h.HumanId, _now.AddMinutes(10));
        Assert.Equal((401, _invalidToken), await GetAsync(h, "Bearer " + foreign));
    }

    [Fact]
    public async Task Revoked_device_is_refused()
    {
        var h = await CreateAsync();
        var token = h.Token();
        Assert.True(await h.Store.RevokeDeviceAsync(h.DeviceId, _now, TestContext.Current.CancellationToken));
        Assert.Equal((401, _invalidToken), await GetAsync(h, "Bearer " + token));
    }

    [Theory]
    [InlineData("suspended")]
    [InlineData("removed")]
    public async Task Suspended_or_removed_human_is_refused(string status)
    {
        var h = await CreateAsync(status);
        Assert.Equal((401, _invalidToken), await GetAsync(h, "Bearer " + h.Token()));
    }

    [Fact]
    public async Task Missing_human_row_is_refused()
    {
        var h = await CreateAsync();
        var token = new SignerAccessTokenIssuer(NewSigner()).Issue(h.DeviceId, Guid.NewGuid(), _now.AddMinutes(10));
        Assert.Equal((401, _invalidToken), await GetAsync(h, "Bearer " + token));
    }

    [Fact]
    public async Task Missing_header_malformed_token_and_non_bearer_scheme_are_refused()
    {
        var h = await CreateAsync();
        Assert.Equal((401, _invalidToken), await GetAsync(h, authorization: null));
        Assert.Equal((401, _invalidToken), await GetAsync(h, "Bearer "));
        Assert.Equal((401, _invalidToken), await GetAsync(h, "Bearer not-a-token"));
        Assert.Equal((401, _invalidToken), await GetAsync(h, "Basic " + h.Token()));
        Assert.Equal((401, _invalidToken), await GetAsync(h, h.Token()));
    }

    [Fact]
    public async Task All_refusals_answer_byte_identical_bodies()
    {
        var h = await CreateAsync();
        var suspended = await CreateAsync("suspended");
        var bodies = new[]
        {
            (await GetAsync(h, null)).Body,
            (await GetAsync(h, "Bearer garbage")).Body,
            (await GetAsync(h, "Bearer " + h.Token(_now.AddMinutes(-1)))).Body,
            (await GetAsync(suspended, "Bearer " + suspended.Token())).Body,
        };
        Assert.All(bodies, b => Assert.Equal(_invalidToken, b));
        Assert.Single(bodies.Distinct());
    }

    [Fact]
    public async Task Plaintext_request_from_another_machine_is_forbidden()
    {
        var h = await CreateAsync();
        var (status, body) = await GetAsync(h, "Bearer " + h.Token(), remote: IPAddress.Parse("203.0.113.9"));

        Assert.Equal(403, status);
        Assert.Equal("""{"error":"forbidden"}""", body);
    }

    [Fact]
    public async Task Https_request_from_another_machine_is_served()
    {
        var h = await CreateAsync();
        var (status, _) = await GetAsync(h, "Bearer " + h.Token(), remote: IPAddress.Parse("203.0.113.9"), https: true);
        Assert.Equal(200, status);
    }

    [Fact]
    public async Task No_verifier_answers_503()
    {
        var h = await CreateAsync(withVerifier: false);
        var (status, body) = await GetAsync(h, "Bearer " + h.Token());

        Assert.Equal(503, status);
        Assert.Equal("""{"error":"unavailable"}""", body);
    }
}
