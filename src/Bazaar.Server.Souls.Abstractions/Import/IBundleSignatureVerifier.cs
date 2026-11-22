using Bazaar.Contracts.Soul;

namespace Bazaar.Server.Souls.Import;

/// <summary>Outcome of checking a bundle signature.</summary>
public enum BundleSignatureStatus
{
    /// <summary>The signature is valid for the exact manifest bytes under a trusted key.</summary>
    Valid,

    /// <summary>The signing key is not trusted by this instance.</summary>
    UnknownKey,

    /// <summary>The signature does not verify, or the algorithm is not one this verifier accepts.</summary>
    Invalid,
}

/// <summary>
/// Verifies the detached signature over the exact bytes of <c>manifest.json</c>. The production implementation (real keys,
/// key trust roots) is not yet built; tests use a fake. Implementations must be fail-closed: anything not clearly
/// <see cref="BundleSignatureStatus.Valid"/> is treated as a rejection.
/// </summary>
public interface IBundleSignatureVerifier
{
    /// <summary>Checks <paramref name="signature"/> against <paramref name="manifestBytes"/>.</summary>
    BundleSignatureStatus Verify(ReadOnlySpan<byte> manifestBytes, BundleSignature signature);
}
