namespace Bazaar.Server.Security.Erasure;

/// <summary>
/// Signs and verifies erasure proofs (design doc 48, section 9). The real key is held by the Identity Service, so the
/// erasure path only sees this interface. This change defines the contract and holds no key.
/// </summary>
public interface IErasureProofSigner
{
    /// <summary>Identifier of the signing key, stored with each proof so a verifier knows which key to check.</summary>
    string KeyId { get; }

    /// <summary>Signs the bytes of a proof (<see cref="ErasureProof.SignedBytes"/>).</summary>
    byte[] Sign(ReadOnlyMemory<byte> data);

    /// <summary>Returns true when <paramref name="signature"/> is a valid signature over <paramref name="data"/>.</summary>
    bool Verify(ReadOnlyMemory<byte> data, ReadOnlyMemory<byte> signature);
}

/// <summary>Checks an erasure proof against a signer.</summary>
public static class ErasureProofVerifier
{
    /// <summary>
    /// Returns true when the proof names the signer's key and <paramref name="signature"/> is valid over the proof's
    /// canonical bytes. A proof signed by a different key id is rejected before the signature is checked.
    /// </summary>
    public static bool Verify(ErasureProof proof, byte[] signature, IErasureProofSigner signer)
    {
        ArgumentNullException.ThrowIfNull(proof);
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentNullException.ThrowIfNull(signer);

        if (!string.Equals(proof.SigningKeyId, signer.KeyId, StringComparison.Ordinal))
        {
            return false;
        }

        return signer.Verify(proof.SignedBytes(), signature);
    }
}
