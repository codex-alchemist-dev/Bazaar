using System.Security.Cryptography;

namespace Bazaar.Server.Modules.Trust;

/// <summary>
/// Produces the signature of a module package: the publisher side of <see cref="EcdsaModuleSignatureVerifier"/>.
/// It signs the canonical form (<see cref="ModuleCanonicalForm"/>) with ECDSA P-256 and writes the IEEE P1363 signature
/// (64 bytes) that the verifier checks. The signer key id is not signed (see the canonical form remarks), so the caller
/// names the key the package is verified under.
/// </summary>
/// <remarks>
/// The private key is an input. This class never generates, loads or stores a key: the release flow passes the key in
/// at release time (doc 49 section 3), and tests pass an ephemeral key.
/// </remarks>
public static class ModulePackageSigner
{
    /// <summary>Returns the package with its signature set. Every other field is copied unchanged.</summary>
    /// <param name="package">The package to sign. Its <see cref="ModulePackage.Signature"/> is replaced.</param>
    /// <param name="privateKey">The publisher's ECDSA P-256 private key.</param>
    public static ModulePackage Sign(ModulePackage package, ECDsa privateKey)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(privateKey);

        if (privateKey.KeySize != 256 || privateKey.ExportParameters(includePrivateParameters: false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
        {
            throw new ArgumentException("The module signing key must be ECDSA P-256.", nameof(privateKey));
        }

        var message = ModuleCanonicalForm.Build(package);
        var signature = privateKey.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return package with { Signature = signature };
    }
}
