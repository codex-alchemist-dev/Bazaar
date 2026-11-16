using System.Security.Cryptography;

namespace Bazaar.Client.Core.Updates;

/// <summary>Verifies a signature over a package with the company's signing key. Implemented per platform.</summary>
public interface IUpdateSignatureVerifier
{
    /// <summary>True when <paramref name="signature"/> is a valid signature of <paramref name="package"/> by the company key.</summary>
    bool Verify(ReadOnlySpan<byte> package, ReadOnlySpan<byte> signature);
}

/// <summary>Checks a downloaded package against its manifest: the SHA-256 must match, then the signature must verify.</summary>
public static class UpdateVerifier
{
    /// <summary>Returns true only when the hash matches the manifest and the signature verifies.</summary>
    public static bool Verify(UpdateManifest manifest, ReadOnlySpan<byte> package, IUpdateSignatureVerifier signatures)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(signatures);

        if (!TryDecodeHash(manifest.Sha256Hex, out var expected))
        {
            return false;
        }

        Span<byte> actual = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(package, actual);
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
        {
            return false;
        }

        return signatures.Verify(package, manifest.Signature);
    }

    private static bool TryDecodeHash(string hex, out byte[] bytes)
    {
        bytes = [];
        if (hex.Length != SHA256.HashSizeInBytes * 2)
        {
            return false;
        }

        try
        {
            bytes = Convert.FromHexString(hex);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
