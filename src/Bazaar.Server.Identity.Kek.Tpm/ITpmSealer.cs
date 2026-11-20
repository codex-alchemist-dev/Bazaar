namespace Bazaar.Server.Identity.Kek.Tpm;

/// <summary>Where the host's TPM runs: real hardware, or a virtual TPM whose state the hypervisor operator can read.</summary>
public enum TpmCustodyClass
{
    /// <summary>A hardware (or firmware) TPM on this host.</summary>
    Hardware = 0,

    /// <summary>A vTPM (swtpm, Hyper-V, QEMU). Weaker custody; it never counts as hardware custody for the doc 48 floors.</summary>
    Virtual = 1,
}

/// <summary>Seals and unseals a small secret under the TPM. The seam keeps the provider testable without a TPM.</summary>
public interface ITpmSealer
{
    /// <summary>Returns the TPM-sealed form of <paramref name="plaintext"/>, bound to the policy in <paramref name="options"/>.</summary>
    byte[] Seal(ReadOnlySpan<byte> plaintext, TpmOptions options);

    /// <summary>
    /// Returns the plaintext of <paramref name="sealedBytes"/>. Throws <see cref="System.Security.Cryptography.CryptographicException"/> when the TPM
    /// will not unseal it (wrong chip, failed policy, damaged blob). It never returns a guess.
    /// </summary>
    byte[] Unseal(ReadOnlySpan<byte> sealedBytes, TpmOptions options);
}

/// <summary>Tells whether this host is itself a virtual machine, which means its TPM is virtual.</summary>
public interface IVirtualizationProbe
{
    /// <summary>The custody class of this host's TPM.</summary>
    TpmCustodyClass Detect();
}
