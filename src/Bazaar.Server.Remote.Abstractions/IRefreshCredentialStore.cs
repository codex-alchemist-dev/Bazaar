namespace Bazaar.Server.Components.Remote;

/// <summary>A device paired to a human (doc 32 <c>device</c> table). <paramref name="RevokedAt"/> is set once and never cleared.</summary>
public sealed record DeviceRecord(Guid Id, Guid HumanId, string Label, string Class, int Revision, DateTimeOffset? RevokedAt);

/// <summary>A person on the instance (doc 32 <c>human</c> table). <paramref name="Role"/> is <c>owner</c> or <c>coworker</c>; <paramref name="Status"/> is <c>active</c>, <c>suspended</c> or <c>removed</c>.</summary>
public sealed record HumanRecord(Guid Id, string DisplayName, string Role, string Status);

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

/// <summary>What suspending a person changed (doc 32, person suspension; PROPOSED). <paramref name="RevokedDevices"/> are the devices revoked by this call, not ones that were already revoked.</summary>
public sealed record SuspendResult(bool Changed, IReadOnlyList<Guid> RevokedDevices);

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

    /// <summary>The human, or null. Read-only; used by <c>GET /auth/whoami</c> (doc 32 section 5).</summary>
    Task<HumanRecord?> FindHumanAsync(Guid humanId, CancellationToken ct = default);

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

    /// <summary>
    /// Sets the person <c>suspended</c> (only from <c>active</c>, and never an owner) and, in the same transaction, revokes every device of
    /// the person that is not yet revoked and every approval key of the person. Returns <c>Changed = false</c> and writes nothing otherwise.
    /// </summary>
    Task<SuspendResult> SuspendHumanAsync(Guid humanId, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>
    /// Sets a <c>suspended</c> person back to <c>active</c>. Returns false when the person is not suspended (removed stays removed).
    /// Revoked devices and approval keys stay revoked: the person pairs again.
    /// </summary>
    Task<bool> UnsuspendHumanAsync(Guid humanId, CancellationToken ct = default);
}
