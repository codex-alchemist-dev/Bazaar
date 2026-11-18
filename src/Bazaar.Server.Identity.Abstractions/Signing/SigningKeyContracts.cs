namespace Bazaar.Server.Identity.Signing;

/// <summary>
/// One row of the access-token signing key (<c>soul_dek</c>, scope <c>access-token</c>). <see cref="WrappedDek"/> is null once the
/// key is past its grace period and its wrap has been cleared; <see cref="RetiredAt"/> is null only for the live key.
/// </summary>
public sealed record SigningKeyRow(
    Guid DekId,
    string Scope,
    byte[]? WrappedDek,
    string KekId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? RetiredAt);

/// <summary>A new live signing key to insert. <see cref="WrappedDek"/> is the key wrapped by the host key-encryption key.</summary>
public sealed record NewSigningKey(
    Guid DekId,
    string Scope,
    byte[] WrappedDek,
    string KekId,
    DateTimeOffset CreatedAt);

/// <summary>
/// Persistence for signing keys. Implementations are SQL-first (Postgres) or in memory (tests and Development). A scope has at most one
/// live row.
/// </summary>
public interface ISigningKeyStore
{
    /// <summary>The live row for the scope, or null when the scope has no key yet.</summary>
    ValueTask<SigningKeyRow?> GetLiveAsync(string scope, CancellationToken cancellationToken = default);

    /// <summary>Every row for the scope, live and retired, with or without a wrap.</summary>
    ValueTask<IReadOnlyList<SigningKeyRow>> ListAsync(string scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retires the scope's live row at <paramref name="retiredAt"/> and inserts <paramref name="newKey"/> as the new live row, in one
    /// transaction.
    /// </summary>
    ValueTask RotateAsync(NewSigningKey newKey, DateTimeOffset retiredAt, CancellationToken cancellationToken = default);

    /// <summary>Clears the wrapped key of retired rows of the scope that retired on or before <paramref name="retiredOnOrBefore"/>.</summary>
    ValueTask ClearRetiredWrapsAsync(string scope, DateTimeOffset retiredOnOrBefore, CancellationToken cancellationToken = default);
}

/// <summary>
/// Wraps and unwraps signing keys under the host key-encryption key. The associated data is the key's dek_id bytes, so a wrap cannot be
/// moved to another row. Unwrap throws when the wrap, the KEK or the associated data does not match.
/// </summary>
public interface ISigningKeyWrapper
{
    /// <summary>The id of the KEK that wraps keys, stored in <c>soul_dek.kek_id</c>.</summary>
    string KekId { get; }

    /// <summary>Wraps a key.</summary>
    ValueTask<byte[]> WrapAsync(ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> associatedData, CancellationToken cancellationToken = default);

    /// <summary>Unwraps a key. Throws when it cannot.</summary>
    ValueTask<byte[]> UnwrapAsync(ReadOnlyMemory<byte> wrappedKey, ReadOnlyMemory<byte> associatedData, CancellationToken cancellationToken = default);
}

/// <summary>The claims a verified access token carries.</summary>
/// <param name="DeviceId">The device the token was issued to (claim <c>sub</c>).</param>
/// <param name="HumanId">The human that owns the device (claim <c>hum</c>).</param>
public sealed record AccessTokenClaims(Guid DeviceId, Guid HumanId);

/// <summary>
/// Signs and verifies bearer access tokens with the identity signing key. Token format: <c>v1.&lt;kid&gt;.&lt;payload&gt;.&lt;mac&gt;</c>, where
/// kid is the dek_id in "N" form, payload is base64url of <c>{"sub","hum","exp"}</c>, and mac is base64url HMAC-SHA256 over
/// ASCII <c>v1.&lt;kid&gt;.&lt;payload&gt;</c>.
/// </summary>
public interface IAccessTokenSigner
{
    /// <summary>Issues a token for a device and its human that expires at <paramref name="expiresAt"/>.</summary>
    ValueTask<string> SignAsync(Guid deviceId, Guid humanId, DateTimeOffset expiresAt, CancellationToken cancellationToken = default);

    /// <summary>The claims of a valid token, or null for a malformed, unknown-key, forged, expired or unverifiable token.</summary>
    ValueTask<AccessTokenClaims?> VerifyAsync(string token, CancellationToken cancellationToken = default);
}
