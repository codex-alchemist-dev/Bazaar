namespace Bazaar.Secrets;

/// <summary>
/// Wraps and unwraps scope data keys under a key-encryption key (KEK). Custody is behind this interface:
/// the software provider is for tests and host-supplied keys, and DPAPI or OpenBao providers plug in later
/// (the Windows KEK store is parked for the owner, docs 14 and 33).
/// </summary>
public interface IKekProvider
{
    /// <summary>Id of the KEK new wraps use. It is stored beside each wrapped key so old wraps stay openable after rotation.</summary>
    string CurrentKeyId { get; }

    Task<ReadOnlyMemory<byte>> WrapAsync(ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> associatedData, CancellationToken ct);

    Task<ReadOnlyMemory<byte>> UnwrapAsync(string keyId, ReadOnlyMemory<byte> wrapped, ReadOnlyMemory<byte> associatedData, CancellationToken ct);
}
