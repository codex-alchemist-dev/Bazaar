using Bazaar.Server.Wire.Connection;

namespace Bazaar.Server.Components.Remote;

/// <summary>
/// Refresh-credential rotation, reuse detection and device revocation (doc 32 section 5, data and interfaces).
/// </summary>
public interface ITokenService
{
    /// <summary>
    /// Exchanges a live refresh secret for a new access token and a new refresh secret. The presented secret is retired.
    /// Throws <see cref="TokenRefreshException"/> when the secret is refused; no tokens are issued then.
    /// </summary>
    Task<TokenPair> RefreshAsync(string refresh, DeviceInfo device, CancellationToken ct = default);

    /// <summary>
    /// Creates the first token pair for a device that has none: the device is recorded for <paramref name="humanId"/> with the class in
    /// <paramref name="device"/>, and its first refresh credential is issued (doc 32 sections 4 and 5, the native handoff).
    /// Throws <see cref="TokenIssueException"/> when the device already exists or is revoked; nothing is stored then.
    /// </summary>
    Task<TokenPair> IssueAsync(Guid deviceId, Guid humanId, DeviceInfo device, CancellationToken ct = default);

    /// <summary>Revokes a device at once. Idempotent: a device that is already revoked is left unchanged and unaudited.</summary>
    Task RevokeDeviceAsync(Guid deviceId, PrincipalRef by, CancellationToken ct = default);
}

/// <summary>What a successful refresh returns. The refresh secret is shown to the client once; the server keeps only its hash.</summary>
/// <param name="AccessToken">Bearer access token (memory on the client only).</param>
/// <param name="AccessTokenExpiresAt">When the access token stops being valid.</param>
/// <param name="RefreshToken">The new refresh secret.</param>
/// <param name="RefreshTokenExpiresAt">When the new refresh secret stops being valid (sliding, capped by the absolute lifetime).</param>
public sealed record TokenPair(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt);

/// <summary>
/// Produces the access token for a device. The signing key belongs to the identity service (doc 14), so this is an
/// interface here; the host registers the real issuer.
/// </summary>
public interface IAccessTokenIssuer
{
    /// <summary>Issues a bearer access token for a device and its human that expires at <paramref name="expiresAt"/>.</summary>
    string Issue(Guid deviceId, Guid humanId, DateTimeOffset expiresAt);
}

/// <summary>
/// No access-token issuer is configured, so no token can be made. Routes map this to 503 with a generic <c>unavailable</c>; the
/// reason stays in the host configuration.
/// </summary>
public sealed class AccessTokenIssuerUnavailableException() : Exception("No access-token issuer is available.");

/// <summary>Why a refresh was refused. The client sees one generic refusal; the reason goes to the server log and audit only.</summary>
public enum TokenRefreshFailure
{
    /// <summary>No credential matches the presented secret, or the device does not match it.</summary>
    UnknownCredential,

    /// <summary>The credential's sliding or absolute lifetime has passed.</summary>
    ExpiredCredential,

    /// <summary>The device has been revoked.</summary>
    DeviceRevoked,

    /// <summary>A retired credential was presented. The device has been revoked (doc 32 section 5).</summary>
    ReuseDetected,
}

/// <summary>A refresh was refused. Carries only the reason, never the secret.</summary>
public sealed class TokenRefreshException(TokenRefreshFailure failure) : Exception("The refresh credential was refused.")
{
    /// <summary>Why the refresh was refused.</summary>
    public TokenRefreshFailure Failure { get; } = failure;
}

/// <summary>Why a first token pair was not issued. The client sees one generic refusal; the reason goes to the server log only.</summary>
public enum TokenIssueFailure
{
    /// <summary>A device with this id already holds credentials. Issuing again would start a second credential chain.</summary>
    DeviceAlreadyExists,

    /// <summary>The device class is empty or not a class the server accepts.</summary>
    InvalidDevice,
}

/// <summary>A first token pair was refused. Carries only the reason.</summary>
public sealed class TokenIssueException(TokenIssueFailure failure) : Exception("The device credential was refused.")
{
    /// <summary>Why the issue was refused.</summary>
    public TokenIssueFailure Failure { get; } = failure;
}
