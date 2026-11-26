using System.Globalization;
using Bazaar.Server.Http;
using Bazaar.Server.OfflineSync;
using Bazaar.Server.Wire.Connection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Host.Components.OfflineSync;

/// <summary>
/// Authenticates <c>POST /offline-sync/batch</c> with the same steps as the other authenticated HTTP routes (the transport rule,
/// the bearer credential through <see cref="IConnectionAuthenticator"/>, expiry, the HTTP rate bucket) and then binds the call to
/// the device the credential was issued to. A credential that carries no server-assigned device id cannot use this route: the
/// batch's device id would be unverifiable, so the request is refused (403) instead of trusting the body.
/// </summary>
public sealed class HostOfflineSyncAuthenticator(
    IConnectionAuthenticator authenticator,
    IOptions<WireOptions> wire,
    HttpRateLimiter rateLimiter,
    TimeProvider time) : IOfflineSyncRequestAuthenticator
{
    /// <inheritdoc />
    public async ValueTask<OfflineSyncAuthResult> AuthenticateAsync(HttpContext http, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);

        var remote = http.Connection.RemoteIpAddress;
        if (!http.Request.IsHttps && !wire.Value.AllowPlaintextOverlay && remote is not null && !System.Net.IPAddress.IsLoopback(remote))
        {
            return OfflineSyncAuthResult.Refuse(StatusCodes.Status403Forbidden, "forbidden");
        }

        var header = http.Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : null;
        if (string.IsNullOrEmpty(token))
        {
            return OfflineSyncAuthResult.Refuse(StatusCodes.Status401Unauthorized, "unauthorized");
        }

        var auth = await authenticator.AuthenticateAsync(new HelloCredential(token), new DeviceInfo(string.Empty, "http", string.Empty), cancellationToken).ConfigureAwait(false);
        if (!auth.Ok || auth.Principal is null || (auth.ExpiresAt is { } expiry && expiry <= time.GetUtcNow()))
        {
            return OfflineSyncAuthResult.Refuse(StatusCodes.Status401Unauthorized, "unauthorized");
        }

        if (!rateLimiter.TryAcquire(auth.Principal.Id, out var retryAfter))
        {
            http.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
            return OfflineSyncAuthResult.Refuse(StatusCodes.Status429TooManyRequests, "rate_limited");
        }

        if (!Guid.TryParse(auth.DeviceId, out var deviceId) || deviceId == Guid.Empty)
        {
            return OfflineSyncAuthResult.Refuse(StatusCodes.Status403Forbidden, "device_unbound");
        }

        return OfflineSyncAuthResult.Success(new OfflineSyncCaller(auth.Principal.Id, deviceId));
    }
}
