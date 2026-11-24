using System.Security.Cryptography;

namespace Bazaar.Server.Memory.Tools;

/// <summary>ULID note ids: 48 bits of millisecond time, then 80 random bits, Crockford base32 (26 characters).</summary>
internal static class MemoryNoteIds
{
    private const string _alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>A new ULID stamped with <paramref name="at"/>.</summary>
    public static string NewUlid(DateTimeOffset at)
    {
        var millis = (ulong)at.ToUnixTimeMilliseconds();
        Span<byte> random = stackalloc byte[10];
        RandomNumberGenerator.Fill(random);

        UInt128 low = 0;
        foreach (var b in random)
        {
            low = (low << 8) | b;
        }

        UInt128 value = ((UInt128)millis << 80) | low;
        var chars = new char[26];
        for (var i = 25; i >= 0; i--)
        {
            chars[i] = _alphabet[(int)(value & (UInt128)31)];
            value >>= 5;
        }

        return new string(chars);
    }
}
