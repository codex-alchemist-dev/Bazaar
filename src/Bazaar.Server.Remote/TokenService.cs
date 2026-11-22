using Bazaar.PrivacyAudit;
using Bazaar.Server.Wire.Connection;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Components.Remote;

/// <summary>
/// Rotates refresh credentials and detects reuse (doc 32 section 5). Each successful refresh retires the presented secret and
/// issues a new one whose expiry is the sliding window, capped by the absolute lifetime measured from the device's first
/// credential. Presenting a retired secret revokes the device and audits both the reuse and the revocation.
/// </summary>
/// <remarks>
/// Two presentations of the same live secret at once: the second one loses the rotation race and is handled as reuse. That
/// revokes the device, which is the conservative choice (RFC 9700 section 4.14.2 allows it). A client that retries a refresh
/// must reuse the response it already has, not resend the old secret.
/// </remarks>
public sealed class TokenService(
    IRefreshCredentialStore store,
    IAccessTokenIssuer accessTokens,
    PrivacyAuditRecorder audit,
    IOptions<RemoteTokenOptions> options,
    TimeProvider time) : ITokenService
{
    private readonly RemoteTokenOptions _options = ValidatedCopy(options);

    public async Task<TokenPair> RefreshAsync(string refresh, DeviceInfo device, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (string.IsNullOrWhiteSpace(refresh))
            throw new TokenRefreshException(TokenRefreshFailure.UnknownCredential);

        var now = time.GetUtcNow();
        var credential = await store.FindByHashAsync(RefreshCredentialSecret.Hash(refresh), ct).ConfigureAwait(false)
            ?? throw new TokenRefreshException(TokenRefreshFailure.UnknownCredential);
        var record = await store.FindDeviceAsync(credential.DeviceId, ct).ConfigureAwait(false)
            ?? throw new TokenRefreshException(TokenRefreshFailure.UnknownCredential);

        // The secret itself is the proof of identity, so reuse is detected whatever the client says about its device.
        if (credential.RetiredAt is not null)
        {
            await RevokeOnReuseAsync(record, credential, now, ct).ConfigureAwait(false);
            throw new TokenRefreshException(TokenRefreshFailure.ReuseDetected);
        }

        if (!string.Equals(record.Class, device.DeviceClass, StringComparison.Ordinal))
            throw new TokenRefreshException(TokenRefreshFailure.UnknownCredential);
        if (record.RevokedAt is not null)
            throw new TokenRefreshException(TokenRefreshFailure.DeviceRevoked);

        // A person who is not active gets nothing, even if a device row slipped through. A missing row is tolerated (the foreign key
        // guarantees one in Postgres); only a row that says suspended or removed refuses.
        if (await store.FindHumanAsync(record.HumanId, ct).ConfigureAwait(false) is { Status: not "active" })
            throw new TokenRefreshException(TokenRefreshFailure.PersonNotActive);

        var absoluteEnd = credential.ChainStartedAt + TimeSpan.FromDays(_options.RefreshAbsoluteDays);
        if (credential.ExpiresAt <= now || absoluteEnd <= now)
            throw new TokenRefreshException(TokenRefreshFailure.ExpiredCredential);

        var nextSecret = RefreshCredentialSecret.Generate();
        var nextExpiresAt = Min(now + TimeSpan.FromDays(_options.RefreshSlidingDays), absoluteEnd);
        var nextId = Guid.CreateVersion7(now);

        // Issue the access token before rotating. If the issuer is unavailable it throws here, and the presented secret stays
        // live for a retry. Rotating first would retire the secret, lose the new one, and make the retry look like reuse.
        var accessExpiresAt = now + TimeSpan.FromMinutes(_options.AccessTokenMinutes);
        var accessToken = accessTokens.Issue(record.Id, record.HumanId, accessExpiresAt);

        var rotated = await store.TryRotateAsync(
            record.Id,
            credential.Id,
            nextId,
            RefreshCredentialSecret.Hash(nextSecret),
            now,
            nextExpiresAt,
            ct).ConfigureAwait(false);

        if (!rotated)
        {
            // Either a concurrent revocation won, or a concurrent refresh already retired this secret.
            var latest = await store.FindDeviceAsync(record.Id, ct).ConfigureAwait(false);
            if (latest?.RevokedAt is not null)
                throw new TokenRefreshException(TokenRefreshFailure.DeviceRevoked);
            await RevokeOnReuseAsync(record, credential, now, ct).ConfigureAwait(false);
            throw new TokenRefreshException(TokenRefreshFailure.ReuseDetected);
        }

        return new TokenPair(accessToken, accessExpiresAt, nextSecret, nextExpiresAt);
    }

    public async Task<TokenPair> IssueAsync(Guid deviceId, Guid humanId, DeviceInfo device, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (deviceId == Guid.Empty || humanId == Guid.Empty || string.IsNullOrWhiteSpace(device.DeviceClass))
            throw new TokenIssueException(TokenIssueFailure.InvalidDevice);
        if (await store.FindDeviceAsync(deviceId, ct).ConfigureAwait(false) is not null)
            throw new TokenIssueException(TokenIssueFailure.DeviceAlreadyExists);

        if (await store.FindHumanAsync(humanId, ct).ConfigureAwait(false) is { Status: not "active" })
            throw new TokenIssueException(TokenIssueFailure.PersonNotActive);

        var now = time.GetUtcNow();
        var secret = RefreshCredentialSecret.Generate();
        var refreshExpiresAt = Min(now + TimeSpan.FromDays(_options.RefreshSlidingDays), now + TimeSpan.FromDays(_options.RefreshAbsoluteDays));

        // Issue the access token first: if the issuer is unavailable nothing has been stored, so the caller can retry.
        var accessExpiresAt = now + TimeSpan.FromMinutes(_options.AccessTokenMinutes);
        var accessToken = accessTokens.Issue(deviceId, humanId, accessExpiresAt);

        try
        {
            await store.CreateDeviceAsync(
                new DeviceRecord(deviceId, humanId, device.DeviceClass, device.DeviceClass, 1, null),
                Guid.CreateVersion7(now),
                RefreshCredentialSecret.Hash(secret),
                now,
                refreshExpiresAt,
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            // A concurrent issue for the same device won the insert.
            throw new TokenIssueException(TokenIssueFailure.DeviceAlreadyExists);
        }

        return new TokenPair(accessToken, accessExpiresAt, secret, refreshExpiresAt);
    }

    public async Task RevokeDeviceAsync(Guid deviceId, PrincipalRef by, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(by);
        var device = await store.FindDeviceAsync(deviceId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("No such device.");
        if (device.RevokedAt is not null)
            return;

        var now = time.GetUtcNow();
        if (!await store.RevokeDeviceAsync(deviceId, now, ct).ConfigureAwait(false))
            return;

        await audit.DeviceRevokedAsync(ActorOf(by), deviceId.ToString("D"), "revoke_request", ct).ConfigureAwait(false);
    }

    /// <summary>Revokes the device (idempotent) and audits the reuse. The revocation is audited only when it changed the device.</summary>
    private async Task RevokeOnReuseAsync(DeviceRecord device, RefreshCredentialRecord credential, DateTimeOffset now, CancellationToken ct)
    {
        var actor = "device:" + device.Id.ToString("D");
        if (await store.RevokeDeviceAsync(device.Id, now, ct).ConfigureAwait(false))
            await audit.DeviceRevokedAsync(actor, device.Id.ToString("D"), "refresh_reuse", ct).ConfigureAwait(false);
        await audit.TokenReuseDetectedAsync(actor, device.Id.ToString("D"), credential.Id.ToString("D"), ct).ConfigureAwait(false);
    }

    private static string ActorOf(PrincipalRef by) => by.Kind + ":" + by.Id.ToString("D");

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a <= b ? a : b;

    private static RemoteTokenOptions ValidatedCopy(IOptions<RemoteTokenOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var value = options.Value;
        value.Validate();
        return value;
    }
}
