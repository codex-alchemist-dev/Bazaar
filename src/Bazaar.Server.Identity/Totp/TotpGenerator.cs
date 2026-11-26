using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;

namespace Bazaar.Server.Identity.Totp;

/// <summary>
/// RFC 6238 time-based one-time password generator (built on the RFC 4226 HOTP truncation). Stateless and safe to
/// share. The seed is never copied, logged or stored by this type.
/// </summary>
public static class TotpGenerator
{
    /// <summary>Smallest accepted number of digits.</summary>
    public const int MinDigits = 6;

    /// <summary>Largest accepted number of digits.</summary>
    public const int MaxDigits = 10;

    private const int _maxDigestBytes = 64;

    /// <summary>Generates the TOTP code for <paramref name="seed"/> at the given instant.</summary>
    /// <param name="seed">The shared secret as raw bytes (for otpauth seeds, decode with <see cref="Base32.Decode"/>).</param>
    /// <param name="at">The instant to generate for. Must not be before the Unix epoch.</param>
    /// <param name="digits">Code length, 6 to 10. Defaults to 6.</param>
    /// <param name="periodSeconds">Time step in seconds, at least 1. Defaults to 30.</param>
    /// <param name="alg">The HMAC algorithm. Defaults to SHA-1.</param>
    /// <returns>The code, zero-padded to <paramref name="digits"/> characters.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A numeric argument, the algorithm or the instant is out of range.</exception>
    public static string Generate(
        ReadOnlySpan<byte> seed,
        DateTimeOffset at,
        int digits = 6,
        int periodSeconds = 30,
        TotpAlgorithm alg = TotpAlgorithm.Sha1)
    {
        if (digits is < MinDigits or > MaxDigits)
        {
            throw new ArgumentOutOfRangeException(nameof(digits), digits, $"Digits must be between {MinDigits} and {MaxDigits}.");
        }

        if (periodSeconds < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(periodSeconds), periodSeconds, "The period must be at least 1 second.");
        }

        long unixSeconds = at.ToUnixTimeSeconds();
        if (unixSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(at), at, "The instant must not be before the Unix epoch.");
        }

        ulong counter = (ulong)unixSeconds / (ulong)periodSeconds;
        return Hotp(seed, counter, digits, alg);
    }

    private static string Hotp(ReadOnlySpan<byte> seed, ulong counter, int digits, TotpAlgorithm alg)
    {
        Span<byte> message = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(message, counter);

        Span<byte> digest = stackalloc byte[_maxDigestBytes];
        int length = Hmac(alg, seed, message, digest);

        // Dynamic truncation (RFC 4226 section 5.3): the low nibble of the last byte picks a 4-byte window.
        int offset = digest[length - 1] & 0x0F;
        uint binary = BinaryPrimitives.ReadUInt32BigEndian(digest.Slice(offset, 4)) & 0x7FFFFFFF;

        ulong code = binary % Pow10(digits);
        return code.ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0');
    }

    /// <summary>Computes HMAC over <paramref name="message"/> into <paramref name="digest"/> and returns the digest length.</summary>
    [SuppressMessage("Security", "CA5350", Justification = "HMAC-SHA1 is the RFC 6238 default that most services issue; HMAC is not weakened by SHA-1 collisions.")]
    private static int Hmac(TotpAlgorithm alg, ReadOnlySpan<byte> seed, ReadOnlySpan<byte> message, Span<byte> digest)
    {
        switch (alg)
        {
            case TotpAlgorithm.Sha1:
                HMACSHA1.HashData(seed, message, digest);
                return 20;
            case TotpAlgorithm.Sha256:
                HMACSHA256.HashData(seed, message, digest);
                return 32;
            case TotpAlgorithm.Sha512:
                HMACSHA512.HashData(seed, message, digest);
                return 64;
            default:
                throw new ArgumentOutOfRangeException(nameof(alg), alg, "Unknown TOTP algorithm.");
        }
    }

    private static ulong Pow10(int digits)
    {
        ulong result = 1;
        for (int i = 0; i < digits; i++)
        {
            result *= 10;
        }

        return result;
    }
}
