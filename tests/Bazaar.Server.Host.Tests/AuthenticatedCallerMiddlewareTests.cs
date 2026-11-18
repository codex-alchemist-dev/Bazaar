using System.Security.Cryptography;
using Bazaar.Server.Components.Remote;
using Bazaar.Server.Host.Auth;
using Bazaar.Server.Http.Auth;
using Bazaar.Server.Identity.Signing;
using Microsoft.AspNetCore.Http;

namespace Bazaar.Server.Host.Tests;

/// <summary>The caller middleware: a verified, unrevoked device with a matching human sets the caller; every other case leaves none (doc 32 section 5).</summary>
public sealed class AuthenticatedCallerMiddlewareTests
{
    private static readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

    private sealed class Harness
    {
        public required SignerAccessTokenIssuer Issuer { get; init; }
        public required SignerAccessTokenVerifier Verifier { get; init; }
        public required InMemoryRefreshCredentialStore Store { get; init; }
        public required Guid DeviceId { get; init; }
        public required Guid HumanId { get; init; }
    }

    /// <summary>A production signer over an in-memory key store (a fresh key per call, so each harness is its own process).</summary>
    private static AccessTokenSigner NewSigner() =>
        new AccessTokenSigner(new InMemorySigningKeyStore(), new SigningKeyKekWrapper(new TestKek()), TimeProvider.System);

    /// <summary>Registers a device with <paramref name="humanId"/> in an in-memory store and returns the signer that signs for it.</summary>
    private static async Task<Harness> CreateAsync(Guid? deviceId = null, Guid? humanId = null)
    {
        var signer = NewSigner();
        var issuer = new SignerAccessTokenIssuer(signer);
        var store = new InMemoryRefreshCredentialStore();
        var device = new DeviceRecord(deviceId ?? Guid.NewGuid(), humanId ?? Guid.NewGuid(), "test-device", "desktop", 1, null);
        await store.CreateDeviceAsync(device, Guid.NewGuid(), RandomNumberGenerator.GetBytes(32), _now, _now.AddDays(30));
        return new Harness
        {
            Issuer = issuer,
            Verifier = new SignerAccessTokenVerifier(signer),
            Store = store,
            DeviceId = device.Id,
            HumanId = device.HumanId,
        };
    }

    /// <summary>Runs the middleware over a request with the given Authorization header (or none) and returns the context after it.</summary>
    private static async Task<HttpContext> RunAsync(Harness h, string? authorization)
    {
        var context = new DefaultHttpContext();
        if (authorization is not null) context.Request.Headers.Authorization = authorization;

        var middleware = new AuthenticatedCallerMiddleware(_ => Task.CompletedTask);
        await middleware.InvokeAsync(context, h.Verifier, h.Store);
        return context;
    }

    [Fact]
    public async Task Valid_token_sets_the_caller()
    {
        var h = await CreateAsync();
        var token = h.Issuer.Issue(h.DeviceId, h.HumanId, _now.AddMinutes(10));

        var context = await RunAsync(h, "Bearer " + token);

        Assert.Equal(new AuthenticatedCaller(h.DeviceId, h.HumanId), context.GetAuthenticatedCaller());
    }

    [Fact]
    public async Task Bearer_prefix_is_case_insensitive()
    {
        var h = await CreateAsync();
        var token = h.Issuer.Issue(h.DeviceId, h.HumanId, _now.AddMinutes(10));

        var context = await RunAsync(h, "bearer " + token);

        Assert.NotNull(context.GetAuthenticatedCaller());
    }

    [Fact]
    public async Task Expired_token_sets_no_caller()
    {
        var h = await CreateAsync();
        var token = h.Issuer.Issue(h.DeviceId, h.HumanId, _now.AddMinutes(-1));

        var context = await RunAsync(h, "Bearer " + token);

        Assert.Null(context.GetAuthenticatedCaller());
    }

    [Theory]
    [InlineData("not-a-token")]
    [InlineData("a.b")]
    [InlineData("Bearer ")]
    public async Task Malformed_token_sets_no_caller(string authorization)
    {
        var h = await CreateAsync();

        var context = await RunAsync(h, authorization);

        Assert.Null(context.GetAuthenticatedCaller());
    }

    [Fact]
    public async Task Token_signed_by_another_process_sets_no_caller()
    {
        var h = await CreateAsync();
        var otherProcess = new SignerAccessTokenIssuer(NewSigner());
        var token = otherProcess.Issue(h.DeviceId, h.HumanId, _now.AddMinutes(10));

        var context = await RunAsync(h, "Bearer " + token);

        Assert.Null(context.GetAuthenticatedCaller());
    }

    [Fact]
    public async Task Revoked_device_sets_no_caller()
    {
        var h = await CreateAsync();
        var token = h.Issuer.Issue(h.DeviceId, h.HumanId, _now.AddMinutes(10));
        Assert.True(await h.Store.RevokeDeviceAsync(h.DeviceId, _now, TestContext.Current.CancellationToken));

        var context = await RunAsync(h, "Bearer " + token);

        Assert.Null(context.GetAuthenticatedCaller());
    }

    [Fact]
    public async Task Human_that_does_not_own_the_device_sets_no_caller()
    {
        var h = await CreateAsync();
        var otherHuman = Guid.NewGuid();
        var token = h.Issuer.Issue(h.DeviceId, otherHuman, _now.AddMinutes(10));

        var context = await RunAsync(h, "Bearer " + token);

        Assert.Null(context.GetAuthenticatedCaller());
    }

    [Fact]
    public async Task Unknown_device_sets_no_caller()
    {
        var h = await CreateAsync();
        var token = h.Issuer.Issue(Guid.NewGuid(), h.HumanId, _now.AddMinutes(10));

        var context = await RunAsync(h, "Bearer " + token);

        Assert.Null(context.GetAuthenticatedCaller());
    }

    [Fact]
    public async Task Missing_header_sets_no_caller()
    {
        var h = await CreateAsync();

        var context = await RunAsync(h, authorization: null);

        Assert.Null(context.GetAuthenticatedCaller());
    }

    [Fact]
    public async Task Middleware_never_rejects_the_request()
    {
        var h = await CreateAsync();
        var nextCalled = false;
        var middleware = new AuthenticatedCallerMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });

        await middleware.InvokeAsync(new DefaultHttpContext(), h.Verifier, h.Store);

        Assert.True(nextCalled);
    }

    [Fact]
    public void Unavailable_verifier_refuses_every_token()
    {
        var verifier = new SignerAccessTokenVerifier(new UnavailableAccessTokenSigner());

        Assert.False(verifier.TryVerify("anything", out var caller));
        Assert.Null(caller);
    }
}
