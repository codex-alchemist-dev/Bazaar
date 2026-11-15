using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Portability;

/// <summary>
/// Wraps a whole export bundle into one encrypted archive (doc 44 section 5). The key is derived from a passphrase
/// with PBKDF2-SHA256 and the archive is sealed with AES-256-GCM.
/// </summary>
/// <remarks>
/// Archive layout (all integers big-endian):
/// <code>
/// magic "BZEX" (4) | format version (1) | KDF id (1) | PBKDF2 iterations (4) | salt (16) | nonce (12) | tag (16) | ciphertext
/// </code>
/// The first 38 bytes (everything before the tag) are the AES-GCM associated data, so a change to the version,
/// KDF, iteration count, salt or nonce fails authentication just like a change to the ciphertext.
/// The ciphertext is a length-prefixed list of the bundle's files, including manifest.json and any signature files.
/// The passphrase is never logged, stored or included in an error message.
/// </remarks>
public static class ExportBundleCipher
{
    /// <summary>Archive format version written by this code.</summary>
    public const byte FormatVersion = 1;

    /// <summary>Iteration count used when the caller does not pass one.</summary>
    public const int DefaultIterations = 600_000;

    /// <summary>Lowest iteration count accepted, for encrypting and for decrypting an archive.</summary>
    public const int MinIterations = 600_000;

    /// <summary>Highest iteration count accepted, so a hostile archive cannot stall the reader.</summary>
    public const int MaxIterations = 10_000_000;

    /// <summary>Salt length in bytes.</summary>
    public const int SaltBytes = 16;

    /// <summary>AES-GCM nonce length in bytes.</summary>
    public const int NonceBytes = 12;

    /// <summary>AES-GCM tag length in bytes.</summary>
    public const int TagBytes = 16;

    private const byte KdfPbkdf2Sha256 = 1;
    private const int KeyBytes = 32;
    private const int MagicBytes = 4;
    private const int IterationsOffset = 6;
    private const int SaltOffset = 10;
    private const int NonceOffset = SaltOffset + SaltBytes;
    private const int TagOffset = NonceOffset + NonceBytes;
    private const int HeaderBytes = TagOffset + TagBytes;
    private const int AadBytes = TagOffset;
    private const int MaxFileCount = 1_000_000;
    private const int MaxPathBytes = 4096;

    private static ReadOnlySpan<byte> Magic => "BZEX"u8;

    /// <summary>
    /// Encrypts the bundle with a fresh random salt and nonce. The bundle must pass <see cref="ExportBundleWriter.Verify"/>.
    /// </summary>
    public static byte[] Encrypt(ExportBundle bundle, string passphrase, int iterations = DefaultIterations)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        return EncryptWithParameters(bundle, passphrase, iterations, salt, nonce);
    }

    /// <summary>
    /// Encrypts with caller-supplied salt and nonce. Only the test vectors use this; production code must call
    /// <see cref="Encrypt"/>, because reusing a salt and nonce breaks AES-GCM.
    /// </summary>
    internal static byte[] EncryptWithParameters(ExportBundle bundle, string passphrase, int iterations, byte[] salt, byte[] nonce)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(salt);
        ArgumentNullException.ThrowIfNull(nonce);
        ArgumentException.ThrowIfNullOrEmpty(passphrase);
        ValidateIterations(iterations);
        ArgumentOutOfRangeException.ThrowIfNotEqual(salt.Length, SaltBytes);
        ArgumentOutOfRangeException.ThrowIfNotEqual(nonce.Length, NonceBytes);

        var problems = ExportBundleWriter.Verify(bundle);
        if (problems.Count > 0)
        {
            throw new InvalidOperationException("Bundle failed verification: " + string.Join("; ", problems));
        }

        var plaintext = SerializeFiles(bundle.Files);
        var output = new byte[HeaderBytes + plaintext.Length];
        var header = output.AsSpan(0, HeaderBytes);
        Magic.CopyTo(header);
        header[MagicBytes] = FormatVersion;
        header[MagicBytes + 1] = KdfPbkdf2Sha256;
        BinaryPrimitives.WriteUInt32BigEndian(header.Slice(IterationsOffset, 4), (uint)iterations);
        salt.CopyTo(header.Slice(SaltOffset, SaltBytes));
        nonce.CopyTo(header.Slice(NonceOffset, NonceBytes));

        var key = DeriveKey(passphrase, salt, iterations);
        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Encrypt(
                nonce,
                plaintext,
                output.AsSpan(HeaderBytes),
                header.Slice(TagOffset, TagBytes),
                header[..AadBytes]);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        return output;
    }

    /// <summary>
    /// Decrypts an archive made by <see cref="Encrypt"/>. Throws <see cref="CryptographicException"/> when the
    /// passphrase is wrong or any byte was changed, and <see cref="InvalidDataException"/> when the header or the
    /// decrypted content is malformed. The returned bundle has passed <see cref="ExportBundleWriter.Verify"/>.
    /// </summary>
    public static ExportBundle Decrypt(ReadOnlySpan<byte> archive, string passphrase)
    {
        ArgumentException.ThrowIfNullOrEmpty(passphrase);
        if (archive.Length <= HeaderBytes)
        {
            throw new InvalidDataException("Not an encrypted export bundle, or it is truncated.");
        }

        if (!archive[..MagicBytes].SequenceEqual(Magic))
        {
            throw new InvalidDataException("Not an encrypted export bundle: magic does not match.");
        }

        if (archive[MagicBytes] != FormatVersion)
        {
            throw new InvalidDataException("Unsupported export bundle format version: " + archive[MagicBytes]);
        }

        if (archive[MagicBytes + 1] != KdfPbkdf2Sha256)
        {
            throw new InvalidDataException("Unsupported export bundle key derivation.");
        }

        long iterations = BinaryPrimitives.ReadUInt32BigEndian(archive.Slice(IterationsOffset, 4));
        if (iterations < MinIterations || iterations > MaxIterations)
        {
            throw new InvalidDataException("Export bundle iteration count is outside the accepted range.");
        }

        var salt = archive.Slice(SaltOffset, SaltBytes).ToArray();
        var nonce = archive.Slice(NonceOffset, NonceBytes).ToArray();
        var tag = archive.Slice(TagOffset, TagBytes).ToArray();
        var ciphertext = archive[HeaderBytes..].ToArray();
        var aad = archive[..AadBytes].ToArray();

        var plaintext = new byte[ciphertext.Length];
        var key = DeriveKey(passphrase, salt, (int)iterations);
        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, aad);
        }
        catch (CryptographicException)
        {
            // Same message for a wrong passphrase and a modified archive, so the reason is not revealed.
            throw new CryptographicException("Wrong passphrase, or the export bundle was modified.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        var files = DeserializeFiles(plaintext);
        var manifestFile = files.FirstOrDefault(f => f.Path == ExportBundleWriter.ManifestFileName)
            ?? throw new InvalidDataException("Export bundle has no manifest.json.");
        var bundle = new ExportBundle(
            Encoding.UTF8.GetString(manifestFile.Content),
            files.OrderBy(f => f.Path, StringComparer.Ordinal).ToList());

        var problems = ExportBundleWriter.Verify(bundle);
        if (problems.Count > 0)
        {
            throw new InvalidDataException("Decrypted bundle failed verification: " + string.Join("; ", problems));
        }

        return bundle;
    }

    private static void ValidateIterations(int iterations)
    {
        if (iterations < MinIterations || iterations > MaxIterations)
        {
            throw new ArgumentOutOfRangeException(
                nameof(iterations),
                $"Iterations must be between {MinIterations} and {MaxIterations}.");
        }
    }

    private static byte[] DeriveKey(string passphrase, byte[] salt, int iterations)
    {
        var passphraseBytes = Encoding.UTF8.GetBytes(passphrase);
        try
        {
            return Rfc2898DeriveBytes.Pbkdf2(passphraseBytes, salt, iterations, HashAlgorithmName.SHA256, KeyBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passphraseBytes);
        }
    }

    private static byte[] SerializeFiles(IReadOnlyList<BundleFile> files)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(files.Count);
            foreach (var file in files)
            {
                var path = Encoding.UTF8.GetBytes(file.Path);
                writer.Write(path.Length);
                writer.Write(path);
                writer.Write((long)file.Content.Length);
                writer.Write(file.Content);
            }
        }

        return stream.ToArray();
    }

    private static List<BundleFile> DeserializeFiles(byte[] payload)
    {
        try
        {
            using var stream = new MemoryStream(payload, writable: false);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            var count = reader.ReadInt32();
            if (count < 0 || count > MaxFileCount)
            {
                throw new InvalidDataException("Export bundle file count is out of range.");
            }

            var files = new List<BundleFile>(count);
            for (var i = 0; i < count; i++)
            {
                var pathLength = reader.ReadInt32();
                if (pathLength <= 0 || pathLength > MaxPathBytes)
                {
                    throw new InvalidDataException("Export bundle path length is out of range.");
                }

                var path = Encoding.UTF8.GetString(ReadExact(reader, pathLength));
                var contentLength = reader.ReadInt64();
                if (contentLength < 0 || contentLength > stream.Length - stream.Position)
                {
                    throw new InvalidDataException("Export bundle content length is out of range.");
                }

                files.Add(new BundleFile(path, ReadExact(reader, (int)contentLength)));
            }

            if (stream.Position != stream.Length)
            {
                throw new InvalidDataException("Export bundle has trailing data.");
            }

            return files;
        }
        catch (IOException ex)
        {
            throw new InvalidDataException("Export bundle content is truncated.", ex);
        }
    }

    private static byte[] ReadExact(BinaryReader reader, int length)
    {
        var buffer = reader.ReadBytes(length);
        if (buffer.Length != length)
        {
            throw new InvalidDataException("Export bundle content is truncated.");
        }

        return buffer;
    }
}
