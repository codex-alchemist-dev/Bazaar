namespace Bazaar.Server.SignIn.WebAuthn.Credentials;

/// <summary>
/// A stored passkey. Public material only: the credential id, the public key (SubjectPublicKeyInfo DER), the sign count, a label and
/// the principal and device ids. <see cref="DeviceId"/> is the device the sign-in handoff carries, so revoking it ends sessions.
/// </summary>
public sealed record StoredPasskeyCredential(
    Guid Id,
    Guid PrincipalId,
    Guid DeviceId,
    byte[] CredentialId,
    byte[] PublicKey,
    long SignCount,
    string Label,
    DateTimeOffset CreatedAt,
    DateTimeOffset? RevokedAt = null);

/// <summary>Storage for passkeys. Implementations never see a private key and must not log credential ids or keys.</summary>
public interface IPasskeyCredentialStore
{
    /// <summary>The live (unrevoked) credential with this credential id, or null.</summary>
    Task<StoredPasskeyCredential?> FindByCredentialIdAsync(byte[] credentialId, CancellationToken ct = default);

    /// <summary>Adds a credential. False when the credential id is already registered.</summary>
    Task<bool> AddAsync(StoredPasskeyCredential credential, CancellationToken ct = default);

    /// <summary>
    /// Compare-and-set of the sign count: stores <paramref name="newCount"/> only while the stored count still equals
    /// <paramref name="expectedCount"/> and the credential is live. False means another assertion won the race or the key was revoked.
    /// </summary>
    Task<bool> TryAdvanceSignCountAsync(Guid id, long expectedCount, long newCount, DateTimeOffset usedAt, CancellationToken ct = default);

    /// <summary>The credential ids a principal already has, so the authenticator does not enrol the same key twice.</summary>
    Task<IReadOnlyList<byte[]>> ListCredentialIdsAsync(Guid principalId, CancellationToken ct = default);

    /// <summary>Number of live credentials for a principal.</summary>
    Task<int> CountAsync(Guid principalId, CancellationToken ct = default);

    /// <summary>Marks a credential revoked. False when it is unknown or already revoked.</summary>
    Task<bool> RevokeAsync(Guid id, DateTimeOffset now, CancellationToken ct = default);
}
