using System.Net;
using System.Security.Cryptography;
using System.Text;
using Bazaar.Server.Components.Remote;
using Bazaar.Server.Host.Auth;
using Bazaar.Server.Http.Auth;
using Bazaar.Server.Identity.Signing;
using Bazaar.Server.Wire.Connection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Host.Tests;

/// <summary><c>POST /auth/signout</c> (doc 32 section 5): revokes the calling device only; one 401 for every bad token, 403 for plaintext off the machine, 503 without services.</summary>
public sealed class AuthSignOutEndpointTests
{
    private static readonly DateTimeOffset _now = DateTimeOffset.UtcNow;
    private const string _invalidToken = """{"error":"invalid_token"}""";

    private sealed class RecordingTokenService : ITokenService
    {
        public List<(Guid DeviceId, PrincipalRef By)> Revoked { get; } = [];

        public Task<TokenPair> RefreshAsync(string refresh, DeviceInfo device, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<TokenPair> IssueAsync(Guid deviceId, Guid humanId, DeviceInfo device, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task RevokeDeviceAsync(Guid deviceId, PrincipalRef by, CancellationToken ct = default)
        {
            Revoked.Add((deviceId, by));
            return Task.CompletedTask;
        }
    }

    private sealed class Harness
    {
        public required SignerAccessTokenIssuer Issuer { get; init; }
        public required RecordingTokenService Tokens { get; init; }
        public required ServiceProvider Services { get; init; }
        public Guid DeviceId { get; } = Guid.NewGuid();
        public Guid HumanId { get; } = Guid.NewGuid();

        public string Token(DateTimeOffset? expiresAt = null) => Issuer.Issue(DeviceId, HumanId, expiresAt ?? _now.AddMinutes(10));
    }

    private static AccessTokenSigner NewSigner() =>
        new AccessTokenSigner(new InMemorySigningKeyStore(), new SigningKeyKekWrapper(new TestKek()), TimeProvider.System);

    private static Harness Create(bool withVerifier = true, bool withTokens = true)
    {
        var signer = NewSigner();
        var tokens = new RecordingTokenService();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new WireOptions()));
        if (withTokens) services.AddSingleton<ITokenService>(tokens);
        if (withVerifier) services.AddSingleton<IAccessTokenVerifier>(new SignerAccessTokenVerifier(signer));
        return new Harness { Issuer = new SignerAccessTokenIssuer(signer), Tokens = tokens, Services = services.BuildServiceProvider() };
    }

    private static async Task<(int Status, string Body)> PostAsync(Harness h, string? authorization, IPAddress? remote = null, bool https = false)
    {
        var context = new DefaultHttpContext { RequestServices = h.Services };
        context.Request.Method = "POST";
        context.Request.Path = "/auth/signout";
        context.Request.IsHttps = https;
        context.Connection.RemoteIpAddress = remote ?? IPAddress.Loopback;
        if (authorization is not null) context.Request.Headers.Authorization = authorization;
        context.Response.Body = new MemoryStream();

        var result = await AuthSignOutEndpoint.HandleAsync(context);
        await result.ExecuteAsync(context);
        return (context.Response.StatusCode, Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray()));
    }

    [Fact]
    public async Task Valid_token_revokes_that_device_and_answers_204()
    {
        var h = Create();

        var (status, body) = await PostAsync(h, "Bearer " + h.Token());

        Assert.Equal(204, status);
        Assert.Equal(string.Empty, body);
        var revoked = Assert.Single(h.Tokens.Revoked);
        Assert.Equal(h.DeviceId, revoked.DeviceId);
        Assert.Equal(new PrincipalRef(h.HumanId, "human"), revoked.By);
    }

    [Fact]
    public async Task Signing_out_twice_is_not_an_error()
    {
        var h = Create();
        var token = h.Token();
        Assert.Equal(204, (await PostAsync(h, "Bearer " + token)).Status);
        Assert.Equal(204, (await PostAsync(h, "Bearer " + token)).Status);
    }

    [Fact]
    public async Task Expired_token_is_refused_and_revokes_nothing()
    {
        var h = Create();
        Assert.Equal((401, _invalidToken), await PostAsync(h, "Bearer " + h.Token(_now.AddMinutes(-1))));
        Assert.Empty(h.Tokens.Revoked);
    }

    [Fact]
    public async Task Token_with_a_bad_signature_is_refused_and_revokes_nothing()
    {
        var h = Create();
        var foreign = new SignerAccessTokenIssuer(NewSigner()).Issue(h.DeviceId, h.HumanId, _now.AddMinutes(10));
        Assert.Equal((401, _invalidToken), await PostAsync(h, "Bearer " + foreign));
        Assert.Empty(h.Tokens.Revoked);
    }

    [Fact]
    public async Task Missing_header_and_non_bearer_scheme_are_refused()
    {
        var h = Create();
        Assert.Equal((401, _invalidToken), await PostAsync(h, authorization: null));
        Assert.Equal((401, _invalidToken), await PostAsync(h, "Bearer "));
        Assert.Equal((401, _invalidToken), await PostAsync(h, "Basic " + Convert.ToBase64String(RandomNumberGenerator.GetBytes(8))));
        Assert.Empty(h.Tokens.Revoked);
    }

    [Fact]
    public async Task Plaintext_off_the_machine_is_refused_before_the_token_is_read()
    {
        var h = Create();
        var (status, body) = await PostAsync(h, "Bearer " + h.Token(), remote: IPAddress.Parse("203.0.113.9"));
        Assert.Equal(403, status);
        Assert.Equal("""{"error":"forbidden"}""", body);
        Assert.Empty(h.Tokens.Revoked);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Without_a_verifier_or_token_service_the_route_answers_503(bool withVerifier, bool withTokens)
    {
        var h = Create(withVerifier, withTokens);
        var (status, body) = await PostAsync(h, "Bearer " + h.Token());
        Assert.Equal(503, status);
        Assert.Equal("""{"error":"unavailable"}""", body);
    }
}
