namespace Bazaar.Secrets;

/// <summary>
/// The one server-side store for secrets that are not bootstrap values (doc 14, C05).
/// Implementations check the caller, the scope's agent state and the audit write before any value moves.
/// </summary>
public interface ISecretStore
{
    /// <summary>Stores or replaces a secret. Host callers only.</summary>
    Task PutAsync(SecretCaller caller, SecretScope scope, SecretName name, SecretValue value, CancellationToken ct);

    /// <summary>Reads the latest version of a secret for a stated purpose. Host callers only.</summary>
    Task<SecretValue> GetAsync(SecretCaller caller, SecretScope scope, SecretName name, SecretPurpose purpose, CancellationToken ct);

    /// <summary>Deletes one secret. Host callers only.</summary>
    Task DeleteAsync(SecretCaller caller, SecretScope scope, SecretName name, CancellationToken ct);

    /// <summary>
    /// Destroys an agent's data key and every secret in its scope. The ciphertext stays on disk but cannot be
    /// opened. Allowed in any agent state, so a revoked agent can still be erased.
    /// </summary>
    Task CryptoEraseAsync(SecretCaller caller, AgentId agent, CancellationToken ct);
}
