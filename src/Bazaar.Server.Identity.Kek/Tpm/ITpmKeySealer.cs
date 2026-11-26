namespace Bazaar.Server.Identity.Kek.Tpm;

/// <summary>Seals a 32-byte KEK to the TPM and opens it again. The sealed form is an opaque blob kept in the KEK file.</summary>
public interface ITpmKeySealer
{
    byte[] Seal(byte[] key);

    /// <exception cref="KekCustodyException">The TPM or the tool refused, or the blob is not valid.</exception>
    byte[] Unseal(byte[] sealedBlob);
}
