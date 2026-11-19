namespace Bazaar.Server.Components.Remote;

/// <summary>A device paired to a human (doc 32 <c>device</c> table). <paramref name="RevokedAt"/> is set once and never cleared.</summary>
public sealed record DeviceRecord(Guid Id, Guid HumanId, string Label, string Class, int Revision, DateTimeOffset? RevokedAt);

/// <summary>
/// One issued refresh secret (doc 32 <c>refresh_credential</c>). Only its hash is held. <paramref name="ChainStartedAt"/> is the
/// issue time of the device's first credential, which the absolute lifetime counts from.
/// </summary>
public sealed record RefreshCredentialRecord(
    Guid Id,
    Guid DeviceId,
    DateTimeOffset IssuedAt,
    DateTimeOffset? RetiredAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset ChainStartedAt);

/// <summary>
/// Storage for devices and their refresh credentials (doc 32 section 5 and the <c>device</c> and <c>refresh_credential</c> tables).
/// Each write that must be atomic is one method, so the rotation and revocation rules hold in every implementation.
/// </summary>
public interface IRefreshCredentialStore
{
    /// <summary>The credential whose hash equals <paramref name="hash"/>, live or retired, or null.</summary>
    Task<RefreshCredentialRecord?> FindByHashAsync(byte[] hash, CancellationToken ct = default);

    /// <summary>The device, or null.</summary>
    Task<DeviceRecord?> FindDeviceAsync(Guid deviceId, CancellationToken ct = default);

    /// <summary>Inserts a device and its first live credential in one transaction. Pairing (doc 32 section 3) calls this.</summary>
    Task CreateDeviceAsync(
        DeviceRecord device,
        Guid credentialId,
        byte[] hash,
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt,
        CancellationToken ct = default);

    /// <summary>
    /// Retires <paramref name="currentCredentialId"/> and inserts the next live credential, in one transaction. Returns false,
    /// and changes nothing, when the current credential is already retired or the device is revoked.
    /// </summary>
    Task<bool> TryRotateAsync(
        Guid deviceId,
        Guid currentCredentialId,
        Guid nextCredentialId,
        byte[] nextHash,
        DateTimeOffset now,
        DateTimeOffset nextExpiresAt,
        CancellationToken ct = default);

    /// <summary>
    /// Sets <c>revoked_at</c> and bumps <c>revision</c> when the device is not yet revoked. Returns false when it already was.
    /// </summary>
    Task<bool> RevokeDeviceAsync(Guid deviceId, DateTimeOffset now, CancellationToken ct = default);
}
