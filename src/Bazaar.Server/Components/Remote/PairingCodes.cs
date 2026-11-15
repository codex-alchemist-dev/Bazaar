using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Server.Components.Remote;

/// <summary>
/// The short user code an approver reads out or scans, and the hashes the server keeps for it (doc 32 section 3). The code uses
/// Crockford base32 with eight characters; input is normalized so case, dashes, spaces and the look-alikes I, L and O are forgiven.
/// Only the SHA-256 of the normalized code is stored.
/// </summary>
public static class PairingCodes
{
    /// <summary>Crockford base32: no I, L, O or U.</summary>
    public const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>Characters in a user code (doc 32 section 3, format fixed in v1).</summary>
    public const int UserCodeLength = 8;

    /// <summary>A new user code, canonical form.</summary>
    public static string NewUserCode()
    {
        Span<char> chars = stackalloc char[UserCodeLength];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(chars);
    }

    /// <summary>The canonical form of a typed or scanned code: upper case, no dashes or spaces, look-alikes mapped.</summary>
    public static string Normalize(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var builder = new StringBuilder(input.Length);
        foreach (var raw in input)
        {
            if (raw is '-' or ' ') continue;
            var c = char.ToUpperInvariant(raw);
            builder.Append(c switch
            {
                'I' or 'L' => '1',
                'O' => '0',
                _ => c,
            });
        }
        return builder.ToString();
    }

    /// <summary>True when the normalized text has the right length and only Crockford characters.</summary>
    public static bool IsWellFormed(string normalized)
    {
        if (normalized.Length != UserCodeLength) return false;
        foreach (var c in normalized)
            if (Alphabet.IndexOf(c, StringComparison.Ordinal) < 0) return false;
        return true;
    }

    /// <summary>The 32-byte SHA-256 of a normalized code, as stored in <c>pairing_offer.user_code_hash</c>.</summary>
    public static byte[] HashUserCode(string normalized) => SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
}
