namespace Bazaar.Server.Security.Audit.Checkpoints;

/// <summary>
/// Signs and verifies audit checkpoints. The real implementation lives behind the Identity Service (design doc 48,
/// section 5.2): agents never read the key, so the audit service only sees this interface. Signing is synchronous here
/// because this change defines the contract only; a remote signer will need an async form when it is built.
/// </summary>
public interface IAuditCheckpointSigner
{
    /// <summary>Identifier of the signing key, stored next to each signature so a verifier knows which key to check.</summary>
    string KeyId { get; }

    /// <summary>Signs the bytes of a checkpoint (<see cref="AuditCheckpoint.SignedBytes"/>).</summary>
    byte[] Sign(ReadOnlyMemory<byte> data);

    /// <summary>Returns true when <paramref name="signature"/> is a valid signature over <paramref name="data"/>.</summary>
    bool Verify(ReadOnlyMemory<byte> data, ReadOnlyMemory<byte> signature);
}
