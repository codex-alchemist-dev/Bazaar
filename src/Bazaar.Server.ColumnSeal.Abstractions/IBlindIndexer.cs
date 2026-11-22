namespace Bazaar.Server.ColumnSeal;

/// <summary>The blind index of one value and the key it was computed under.</summary>
/// <param name="KeyId">The DEK the index key was derived from. After a DEK is retired, indexes under the old key stay as they were.</param>
/// <param name="Hash">32 bytes: HMAC-SHA-256 of the normalised value.</param>
public readonly record struct BlindIndexValue(Guid KeyId, ReadOnlyMemory<byte> Hash);

/// <summary>
/// Keyed equality lookup over sealed class C columns (doc 63 section 9): the index is an HMAC of the normalised value under a key
/// derived per scope and column, so equal values give equal indexes inside one scope and column and nothing else.
/// </summary>
/// <remarks>
/// Normalisation is Unicode NFKC, then invariant upper-case, then UTF-8. The index key is derived from the DEK with HKDF-SHA-256 and
/// its own info label; the DEK itself is never used as the HMAC key.
/// </remarks>
public interface IBlindIndexer
{
    /// <summary>Computes the index under the scope's live DEK (creating it if needed). Use it when writing a row.</summary>
    ValueTask<BlindIndexValue> ComputeAsync(string scope, string table, string column, string value, CancellationToken cancellationToken);

    /// <summary>
    /// Computes the index under a named DEK. Use it to look up rows written before a key was retired: compute once per key id
    /// found in the table.
    /// </summary>
    ValueTask<BlindIndexValue> ComputeAsync(string scope, string table, string column, string value, Guid keyId, CancellationToken cancellationToken);
}
