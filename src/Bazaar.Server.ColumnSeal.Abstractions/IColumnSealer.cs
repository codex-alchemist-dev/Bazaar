namespace Bazaar.Server.ColumnSeal;

/// <summary>
/// Seals and opens column values with AES-256-GCM under a scope DEK (doc 63 section 9). Class A and class C columns use this
/// directly. Class B (personal identifiers under the person's key) is a separate seam, not part of this interface yet.
/// </summary>
public interface IColumnSealer
{
    /// <summary>
    /// Seals <paramref name="plaintext"/> under the scope's active DEK, creating the DEK when the scope has none. Each call uses a
    /// fresh random nonce, so sealing the same value twice gives different output.
    /// </summary>
    /// <exception cref="ColumnSealException">The context is invalid, or no key could be obtained.</exception>
    ValueTask<SealedColumnValue> SealAsync(ReadOnlyMemory<byte> plaintext, ColumnSealContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Opens a value. A retired DEK still opens. Any mismatch of ciphertext, nonce, key id, table, column, row key or scope fails the same way.
    /// </summary>
    /// <exception cref="ColumnSealException">The value cannot be opened. The message never contains plaintext or key bytes.</exception>
    ValueTask<byte[]> OpenAsync(SealedColumnValue sealedValue, ColumnSealContext context, CancellationToken cancellationToken);
}
