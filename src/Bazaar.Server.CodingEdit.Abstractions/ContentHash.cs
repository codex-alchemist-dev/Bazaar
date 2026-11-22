using System.Security.Cryptography;

namespace Bazaar.Server.CodingEdit;

/// <summary>SHA-256 helpers. Hashes are lowercase hex strings.</summary>
public static class ContentHash
{
    /// <summary>The SHA-256 of <paramref name="content"/> as lowercase hex.</summary>
    public static string Sha256Hex(ReadOnlySpan<byte> content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    /// <summary>True when <paramref name="value"/> is 64 hexadecimal digits, in either case.</summary>
    public static bool IsSha256Hex(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
