using Bazaar.Server.Host.Components.OfflineSync;
using Bazaar.Server.Http;
using Bazaar.Server.Http.Auth;
using Bazaar.Server.Wire.Connection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Host.Tests;

public sealed class HostOfflineSyncAuthenticatorTests
{
    private sealed class Fixed(AuthResult result) : IConnectionAuthenticator
    {
        public ValueTask<AuthResult> AuthenticateAsync(HelloCredential? credential, DeviceInfo device, CancellationToken cancellationToken) =>
            ValueTask.FromResult(result);
    }

    private static HostOfflineSyncAuthenticator Build(AuthResult result) =>
        new(new Fixed(result), Options.Create(new WireOptions()), new HttpRateLimiter(new HttpSurfaceOptions(), TimeProvider.System), TimeProvider.System);

    private static DefaultHttpContext Request(string? authorization)
    {
        var http = new DefaultHttpContext();
        http.Request.Scheme = "https";
        if (authorization is not null)
        {
            http.Request.Headers.Authorization = authorization;
        }

        return http;
    }

    private static readonly PrincipalRef _principal = new(Guid.NewGuid(), "human");

    [Fact]
    public async Task Missing_bearer_is_401()
    {
        var result = await Build(AuthResult.Success(_principal, Guid.NewGuid().ToString())).AuthenticateAsync(Request(null), CancellationToken.None);

        Assert.Null(result.Caller);
        Assert.Equal(401, result.FailureStatus);
    }

    [Fact]
    public async Task Rejected_credential_is_401()
    {
        var result = await Build(AuthResult.Fail("no")).AuthenticateAsync(Request("Bearer x"), CancellationToken.None);

        Assert.Equal(401, result.FailureStatus);
    }

    [Fact]
    public async Task Credential_without_device_id_is_refused_with_403()
    {
        var result = await Build(AuthResult.Success(_principal)).AuthenticateAsync(Request("Bearer x"), CancellationToken.None);

        Assert.Null(result.Caller);
        Assert.Equal(403, result.FailureStatus);
        Assert.Equal("device_unbound", result.FailureCode);
    }

    [Fact]
    public async Task Credential_with_device_id_binds_the_caller()
    {
        var device = Guid.NewGuid();

        var result = await Build(AuthResult.Success(_principal, device.ToString())).AuthenticateAsync(Request("Bearer x"), CancellationToken.None);

        Assert.Equal(device, result.Caller?.DeviceId);
        Assert.Equal(_principal.Id, result.Caller?.PrincipalId);
    }

    [Fact]
    public async Task Verified_device_bound_caller_is_used_without_the_connection_authenticator()
    {
        var device = Guid.NewGuid();
        var human = Guid.NewGuid();
        var http = Request("Bearer x");
        http.SetAuthenticatedCaller(new AuthenticatedCaller(device, human));

        var result = await Build(AuthResult.Fail("must not be consulted")).AuthenticateAsync(http, CancellationToken.None);

        Assert.Equal(device, result.Caller?.DeviceId);
        Assert.Equal(human, result.Caller?.PrincipalId);
    }

    [Fact]
    public async Task Verified_caller_without_a_device_falls_back_to_the_credential_path()
    {
        var http = Request("Bearer x");
        http.SetAuthenticatedCaller(new AuthenticatedCaller(Guid.Empty, Guid.NewGuid()));

        var result = await Build(AuthResult.Success(_principal)).AuthenticateAsync(http, CancellationToken.None);

        Assert.Equal("device_unbound", result.FailureCode);
    }
}
