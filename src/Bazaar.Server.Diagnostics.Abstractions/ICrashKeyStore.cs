namespace Bazaar.Server.Diagnostics;

/// <summary>A data key and the id it is stored under. The caller zeroes <see cref="DataKey"/> after use.</summary>
/// <param name="KeyId">Stable id of the key row, written next to every payload it seals.</param>
/// <param name="DataKey">The unwrapped 256-bit key.</param>
public sealed record CrashDataKey(Guid KeyId, byte[] DataKey);

/// <summary>
/// The per-person data keys the crash sealer uses (doc 43 section 11.3). The host binds this to the identity key store, so the
/// diagnostics module never references the server library. Destroying a person's key makes <see cref="UnwrapAsync"/> return null.
/// </summary>
public interface ICrashKeyStore
{
    /// <summary>The active key for an owner (a person, or the reserved company owner), created on first use.</summary>
    ValueTask<CrashDataKey> GetOrCreateActiveAsync(Guid ownerId, CancellationToken ct);

    /// <summary>The unwrapped key for a key id, or null when it was destroyed or never existed.</summary>
    ValueTask<byte[]?> UnwrapAsync(Guid keyId, CancellationToken ct);
}
