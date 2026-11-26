namespace Bazaar.Server.SignIn.Passkeys;

/// <summary>A registered passkey of an active human (doc 32 section 6, table <c>passkey_credential</c>).</summary>
/// <param name="Id">The row id.</param>
/// <param name="HumanId">The person the passkey signs in.</param>
/// <param name="CredentialId">The authenticator's credential id.</param>
/// <param name="PublicKey">The credential public key in COSE form.</param>
/// <param name="SignCount">The last accepted signature counter.</param>
public sealed record PasskeyCredential(Guid Id, Guid HumanId, byte[] CredentialId, byte[] PublicKey, long SignCount);

/// <summary>Where passkeys are kept.</summary>
public interface IPasskeyCredentialStore
{
    /// <summary>The credential with this id, or null when it is unknown, revoked or its person is not active.</summary>
    Task<PasskeyCredential?> FindAsync(byte[] credentialId, CancellationToken cancellationToken = default);

    /// <summary>The credential ids this person has registered and not revoked.</summary>
    Task<IReadOnlyList<byte[]>> CredentialIdsAsync(Guid humanId, CancellationToken cancellationToken = default);

    /// <summary>Stores a new credential. False when that credential id is already registered.</summary>
    Task<bool> AddAsync(PasskeyCredential credential, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the new signature counter if the stored one is still <paramref name="expected"/> (compare and set), so two
    /// concurrent assertions cannot both succeed on a cloned credential. False when the stored value had moved.
    /// </summary>
    Task<bool> TryAdvanceSignCountAsync(Guid id, long expected, long updated, CancellationToken cancellationToken = default);
}

/// <summary>The person an access token belongs to, when that person and device are active. The host binds it to its token verifier.</summary>
public interface IEnrolmentCaller
{
    /// <summary>The person's id and display name, or null when the token is not valid or its person or device is not active.</summary>
    Task<EnrolmentPerson?> ResolveAsync(string bearerToken, CancellationToken cancellationToken = default);
}

/// <summary>The signed-in person who is enrolling a passkey.</summary>
public sealed record EnrolmentPerson(Guid HumanId, string DisplayName);
