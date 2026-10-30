namespace Bazaar.Server.Identity.Keys;

/// <summary>
/// Wraps and unwraps data-encryption keys (DEKs) under the host key-encryption key (doc 14, envelope encryption).
/// Implementations are OS-specific (DPAPI on Windows, systemd credentials or TPM on Linux) and live behind this
/// interface. The associated data binds a wrapped DEK to its row (the dek_id), so a wrapped key cannot be moved
/// to another row and still unwrap.
/// </summary>
public interface IKekProvider
{
    /// <summary>Stable id of the KEK that wrapped a key. Stored in <c>soul_dek.kek_id</c> so rotation can find old wraps.</summary>
    string KekId { get; }

    /// <summary>Wraps a DEK. Each call returns different bytes for the same input.</summary>
    ValueTask<byte[]> WrapAsync(ReadOnlyMemory<byte> dek, ReadOnlyMemory<byte> associatedData, CancellationToken cancellationToken);

    /// <summary>Unwraps a DEK. Throws <see cref="KekUnwrapException"/> when the wrap, the KEK or the associated data does not match.</summary>
    ValueTask<byte[]> UnwrapAsync(ReadOnlyMemory<byte> wrappedDek, ReadOnlyMemory<byte> associatedData, CancellationToken cancellationToken);
}
