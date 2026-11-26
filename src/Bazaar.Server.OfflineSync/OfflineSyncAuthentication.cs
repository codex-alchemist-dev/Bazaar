using Microsoft.AspNetCore.Http;

namespace Bazaar.Server.OfflineSync;

/// <summary>Who is calling: the authenticated principal and the device its credential is bound to.</summary>
/// <param name="PrincipalId">The human or agent principal.</param>
/// <param name="DeviceId">The device the credential was issued to. A batch for any other device is refused.</param>
public sealed record OfflineSyncCaller(Guid PrincipalId, Guid DeviceId);

/// <summary>The outcome of authenticating one request. <see cref="Caller"/> is set exactly when the request may proceed.</summary>
/// <param name="Caller">The caller, or null when refused.</param>
/// <param name="FailureStatus">The HTTP status to answer when refused (401, 403, 429).</param>
/// <param name="FailureCode">A short machine code for the error body, never internal detail.</param>
public sealed record OfflineSyncAuthResult(OfflineSyncCaller? Caller, int FailureStatus = StatusCodes.Status401Unauthorized, string FailureCode = "unauthorized")
{
    /// <summary>A successful result.</summary>
    public static OfflineSyncAuthResult Success(OfflineSyncCaller caller) => new(caller);

    /// <summary>A refused request.</summary>
    public static OfflineSyncAuthResult Refuse(int status, string code) => new(null, status, code);
}

/// <summary>
/// Authenticates a request to the offline sync route. The module cannot reference <c>Bazaar.Server</c>, so the host supplies this
/// over the server's connection authenticator, transport rule and HTTP rate bucket, exactly as for the other authenticated routes.
/// </summary>
public interface IOfflineSyncRequestAuthenticator
{
    /// <summary>Authenticates <paramref name="http"/>. Must apply the transport rule and the rate limit, and must bind the caller to a device.</summary>
    ValueTask<OfflineSyncAuthResult> AuthenticateAsync(HttpContext http, CancellationToken cancellationToken);
}
