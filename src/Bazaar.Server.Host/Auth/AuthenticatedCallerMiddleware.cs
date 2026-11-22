using Bazaar.Server.Components.Remote;
using Bazaar.Server.Http.Auth;
using Microsoft.AspNetCore.Http;

namespace Bazaar.Server.Host.Auth;

/// <summary>
/// Reads <c>Authorization: Bearer</c>, verifies the access token, and records the device and human on the request when the device is
/// not revoked and the token's human matches the device's human (doc 32 section 5: revocation is immediate). It never rejects a request:
/// an absent, invalid, or revoked credential leaves no caller, and each route decides what that means.
/// </summary>
public sealed class AuthenticatedCallerMiddleware(RequestDelegate next)
{
    private const string _bearerPrefix = "Bearer ";

    public async Task InvokeAsync(HttpContext http, IAccessTokenVerifier verifier, IRefreshCredentialStore store)
    {
        ArgumentNullException.ThrowIfNull(http);
        var token = BearerToken(http.Request.Headers.Authorization.ToString());
        if (token is not null && verifier.TryVerify(token, out var caller))
        {
            var device = await store.FindDeviceAsync(caller.DeviceId, http.RequestAborted).ConfigureAwait(false);
            if (device is { RevokedAt: null } && device.HumanId == caller.HumanId)
                http.SetAuthenticatedCaller(caller);
        }

        await next(http).ConfigureAwait(false);
    }

    /// <summary>The token after a case-insensitive "Bearer " prefix, or null when the header is absent, not bearer, or empty.</summary>
    internal static string? BearerToken(string header)
    {
        if (!header.StartsWith(_bearerPrefix, StringComparison.OrdinalIgnoreCase)) return null;
        var token = header[_bearerPrefix.Length..].Trim();
        return token.Length == 0 ? null : token;
    }
}

public static class AuthenticatedCallerMiddlewareExtensions
{
    /// <summary>Adds <see cref="AuthenticatedCallerMiddleware"/>. Register before the routes so they see the caller.</summary>
    public static IApplicationBuilder UseBazaarAuthenticatedCaller(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<AuthenticatedCallerMiddleware>();
    }
}
