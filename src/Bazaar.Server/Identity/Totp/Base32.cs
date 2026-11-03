using System.Collections.Generic;

namespace Bazaar.Server.Identity.Totp;

/// <summary>
/// RFC 4648 Base32 decoding for otpauth-style TOTP seeds. Input is case-insensitive; spaces and '=' padding are
/// ignored. Any other character is rejected. Seeds are never written to logs or exception messages.
/// </summary>
public static class Base32
{
    private const string _alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>Decodes a Base32 string into raw bytes.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    /// <exception cref="ArgumentException">The text contains a character outside the RFC 4648 alphabet.</exception>
    public static byte[] Decode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var output = new List<byte>((text.Length * 5) / 8);
        int buffer = 0;
        int bitsLeft = 0;

        foreach (char raw in text)
        {
            if (raw == ' ' || raw == '=')
            {
                continue;
            }

            int value = _alphabet.IndexOf(char.ToUpperInvariant(raw));
            if (value < 0)
            {
                // The offending character is not echoed back, so no part of a seed reaches an exception message.
                throw new ArgumentException("Base32 input contains a character outside the RFC 4648 alphabet.", nameof(text));
            }

            buffer = ((buffer << 5) | value) & 0xFFFF;
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
                bitsLeft -= 8;
                output.Add((byte)((buffer >> bitsLeft) & 0xFF));
            }
        }

        // Leftover bits (fewer than 8) are padding and are discarded.
        return output.ToArray();
    }
}
