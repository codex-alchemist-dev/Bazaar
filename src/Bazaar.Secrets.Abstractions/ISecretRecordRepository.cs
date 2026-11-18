namespace Bazaar.Secrets;

/// <summary>A scope's data key as stored. <see cref="WrappedKey"/> is sealed under the KEK named by <see cref="KeyId"/>.</summary>
public sealed record ScopeKeyRecord(
    string ScopeKey,
    string KeyId,
    ReadOnlyMemory<byte> WrappedKey,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DestroyedAt);

/// <summary>One stored secret version. Ciphertext is sealed under the scope's data key.</summary>
public sealed record SecretRecord(
    string ScopeKey,
    string Name,
    int Version,
    ReadOnlyMemory<byte> Nonce,
    ReadOnlyMemory<byte> Ciphertext,
    ReadOnlyMemory<byte> Tag,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Persistence port for secret records. The Postgres adapter is a schema-scoped role that agents cannot reach.
/// Implementations must make <see cref="TryInsertScopeKeyAsync"/> insert-if-absent so two racing first writes agree on one key.
/// </summary>
public interface ISecretRecordRepository
{
    Task<ScopeKeyRecord?> GetScopeKeyAsync(string scopeKey, CancellationToken ct);

    /// <summary>Inserts the key only when the scope has none. Returns false when another writer won the race.</summary>
    Task<bool> TryInsertScopeKeyAsync(ScopeKeyRecord record, CancellationToken ct);

    /// <summary>Clears the wrapped key and sets <see cref="ScopeKeyRecord.DestroyedAt"/>. Does nothing when the scope has no key.</summary>
    Task EraseScopeKeyAsync(string scopeKey, DateTimeOffset at, CancellationToken ct);

    Task<SecretRecord?> GetSecretAsync(string scopeKey, string name, CancellationToken ct);

    Task PutSecretAsync(SecretRecord record, CancellationToken ct);

    Task<bool> DeleteSecretAsync(string scopeKey, string name, CancellationToken ct);

    Task DeleteScopeSecretsAsync(string scopeKey, CancellationToken ct);
}
