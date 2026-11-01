using System.Security.Cryptography;

namespace Bazaar.Server.Backup.Manifest;

/// <summary>Hash helpers shared by the writer, verifier and codec. Pure.</summary>
internal static class BundleHash
{
    public static string Sha256Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>Human-readable "N bytes sha256:hex" text used in findings.</summary>
    public static string Describe(long length, string sha256) => $"{length} bytes sha256:{sha256}";
}
