using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Server.Portability;

/// <summary>
/// Signs the export manifest with ECDSA P-256 and SHA-256 (doc 44 section 6.3). The signature covers the bytes of
/// manifest.json. Verification needs only the public key.
/// </summary>
/// <remarks>
/// The signature and the public key sit in the bundle's file list but not in the manifest's own file list. A
/// manifest cannot list a signature over itself, so <see cref="ExportBundleWriter.Verify"/> skips these two files.
/// The public key is a DER SubjectPublicKeyInfo, and the signature is the fixed-size P1363 form (64 bytes for P-256).
/// </remarks>
public static class BundleSigner
{
    /// <summary>Path of the signature over manifest.json.</summary>
    public const string SignatureFileName = "manifest.sig";

    /// <summary>Path of the public key (DER SubjectPublicKeyInfo) that verifies <see cref="SignatureFileName"/>.</summary>
    public const string PublicKeyFileName = "manifest.pub";

    private const int _curveKeySize = 256;
    private static readonly HashAlgorithmName _hash = HashAlgorithmName.SHA256;
    private static readonly DSASignatureFormat _format = DSASignatureFormat.IeeeP1363FixedFieldConcatenation;

    /// <summary>True for the two file names that the signer adds to a bundle.</summary>
    public static bool IsSignatureFile(string path) => path is SignatureFileName or PublicKeyFileName;

    /// <summary>
    /// Returns a copy of the bundle with manifest.sig and manifest.pub added. Replaces any earlier signature files.
    /// The bundle must pass <see cref="ExportBundleWriter.Verify"/> first.
    /// </summary>
    public static ExportBundle Sign(ExportBundle bundle, ECDsa privateKey)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(privateKey);

        var problems = ExportBundleWriter.Verify(bundle);
        if (problems.Count > 0)
        {
            throw new InvalidOperationException("Bundle failed verification: " + string.Join("; ", problems));
        }

        var manifestBytes = Encoding.UTF8.GetBytes(bundle.Manifest);
        var signature = privateKey.SignData(manifestBytes, _hash, _format);
        var publicKey = privateKey.ExportSubjectPublicKeyInfo();

        var files = bundle.Files
            .Where(f => !IsSignatureFile(f.Path))
            .Append(new BundleFile(SignatureFileName, signature))
            .Append(new BundleFile(PublicKeyFileName, publicKey))
            .OrderBy(f => f.Path, StringComparer.Ordinal)
            .ToList();
        return bundle with { Files = files };
    }

    /// <summary>
    /// True only when the bundle has a manifest.sig that verifies over manifest.json with the given ECDSA P-256 public
    /// key (DER SubjectPublicKeyInfo). The manifest text and the manifest.json file must both match.
    /// </summary>
    public static bool Verify(ExportBundle bundle, ReadOnlySpan<byte> publicKey)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        var manifestFile = bundle.Files.FirstOrDefault(f => f.Path == ExportBundleWriter.ManifestFileName);
        var signatureFile = bundle.Files.FirstOrDefault(f => f.Path == SignatureFileName);
        if (manifestFile is null || signatureFile is null)
        {
            return false;
        }

        if (!manifestFile.Content.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(bundle.Manifest)))
        {
            return false;
        }

        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(publicKey, out _);
            if (ecdsa.KeySize != _curveKeySize)
            {
                return false;
            }

            return ecdsa.VerifyData(manifestFile.Content, signatureFile.Content, _hash, _format);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}
