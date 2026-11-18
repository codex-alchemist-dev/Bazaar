using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Server.Security.Erasure;

/// <summary>
/// Builds the subject reference that an erasure proof carries (design doc 48, section 9). The reference is a SHA-256 over
/// the salt and the UTF-8 subject id, so the proof names the subject without naming them. There is no unsalted path:
/// a salt of at least <see cref="MinSaltLength"/> bytes is required.
/// </summary>
/// <remarks>
/// The salt and the subject id are each written with a 32-bit length prefix, so the two fields cannot run together.
/// </remarks>
public static class SubjectRefHasher
{
    /// <summary>Smallest salt accepted, in bytes.</summary>
    public const int MinSaltLength = 16;

    /// <summary>Returns the 32-byte salted SHA-256 reference for <paramref name="subjectId"/>.</summary>
    public static byte[] Hash(string subjectId, byte[] salt)
    {
        ArgumentNullException.ThrowIfNull(subjectId);
        ArgumentNullException.ThrowIfNull(salt);

        if (string.IsNullOrWhiteSpace(subjectId))
        {
            throw new ArgumentException("The subject id must not be empty.", nameof(subjectId));
        }

        if (salt.Length < MinSaltLength)
        {
            throw new ArgumentException($"The salt must be at least {MinSaltLength} bytes.", nameof(salt));
        }

        using var stream = new MemoryStream();
        WriteField(stream, salt);
        WriteField(stream, Encoding.UTF8.GetBytes(subjectId));

        return SHA256.HashData(stream.ToArray());
    }

    private static void WriteField(MemoryStream stream, byte[] value)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
        stream.Write(length);
        stream.Write(value);
    }
}
