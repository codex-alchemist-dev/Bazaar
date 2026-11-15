using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace Bazaar.Portability;

/// <summary>
/// Argon2id cost parameters for the passphrase key (doc 44 section 5). They are written into every archive header,
/// so an archive stays readable when the defaults change.
/// </summary>
/// <param name="MemoryKiB">Memory cost in KiB.</param>
/// <param name="TimeCost">Number of passes over memory.</param>
/// <param name="Parallelism">Lanes (degree of parallelism).</param>
public readonly record struct Argon2Parameters(int MemoryKiB, int TimeCost, int Parallelism)
{
    /// <summary>RFC 9106 second recommended profile: 64 MiB, three passes, four lanes.</summary>
    public static Argon2Parameters Default { get; } = new(65_536, 3, 4);
}

/// <summary>
/// Wraps a whole export bundle into one encrypted archive (doc 44 section 5). The key is derived from a passphrase
/// with Argon2id (RFC 9106) and the archive is sealed with AES-256-GCM.
/// </summary>
/// <remarks>
/// Archive layout (all integers big-endian):
/// <code>
/// magic "BZEX" (4) | format version (1) | KDF id (1) | KDF parameter version (1) | memory KiB (4) |
/// time cost (4) | parallelism (1) | salt (16) | nonce (12) | tag (16) | ciphertext
/// </code>
/// The KDF id 1 means Argon2id, and the KDF parameter version 1 means the three cost fields above. Every byte
/// before the tag (44 bytes) is the AES-GCM associated data, so a change to any header field fails authentication
/// just like a change to the ciphertext. The ciphertext is a length-prefixed list of the bundle's files, including
/// manifest.json and any signature files. The passphrase is never logged, stored or included in an error message.
/// </remarks>
public static class ExportBundleCipher
{
    /// <summary>Archive format version written by this code. The earlier PBKDF2 draft (version 1) is not read.</summary>
    public const byte FormatVersion = 2;

    /// <summary>KDF id for Argon2id.</summary>
    public const byte KdfArgon2id = 1;

    /// <summary>Version of the Argon2id parameter block that follows the KDF id.</summary>
    public const byte KdfParametersVersion = 1;

    /// <summary>Smallest memory cost accepted, for encrypting and for decrypting (19 MiB, the OWASP Argon2id minimum).</summary>
    public const int MinMemoryKiB = 19_456;

    /// <summary>Largest memory cost accepted, so a hostile archive cannot exhaust the reader (1 GiB).</summary>
    public const int MaxMemoryKiB = 1_048_576;

    /// <summary>Smallest time cost accepted.</summary>
    public const int MinTimeCost = 2;

    /// <summary>Largest time cost accepted.</summary>
    public const int MaxTimeCost = 10;

    /// <summary>Largest parallelism accepted.</summary>
    public const int MaxParallelism = 16;

    /// <summary>Salt length in bytes.</summary>
    public const int SaltBytes = 16;

    /// <summary>AES-GCM nonce length in bytes.</summary>
    public const int NonceBytes = 12;

    /// <summary>AES-GCM tag length in bytes.</summary>
    public const int TagBytes = 16;

    private const int _keyBytes = 32;
    private const int _magicBytes = 4;
    private const int _kdfIdOffset = 5;
    private const int _kdfParametersVersionOffset = 6;
    private const int _memoryOffset = 7;
    private const int _timeCostOffset = 11;
    private const int _parallelismOffset = 15;
    private const int _saltOffset = 16;
    private const int _nonceOffset = _saltOffset + SaltBytes;
    private const int _tagOffset = _nonceOffset + NonceBytes;
    private const int _headerBytes = _tagOffset + TagBytes;
    private const int _aadBytes = _tagOffset;
    private const int _maxFileCount = 1_000_000;
    private const int _maxPathBytes = 4096;

    private static ReadOnlySpan<byte> Magic => "BZEX"u8;

    /// <summary>
    /// Encrypts the bundle with a fresh random salt and nonce. The bundle must pass <see cref="ExportBundleWriter.Verify"/>.
    /// </summary>
    public static byte[] Encrypt(ExportBundle bundle, string passphrase, Argon2Parameters? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        return EncryptWithParameters(bundle, passphrase, parameters ?? Argon2Parameters.Default, salt, nonce);
    }

    /// <summary>
    /// Encrypts with caller-supplied salt and nonce. Only the test vectors use this; production code must call
    /// <see cref="Encrypt"/>, because reusing a salt and nonce breaks AES-GCM.
    /// </summary>
    internal static byte[] EncryptWithParameters(
        ExportBundle bundle,
        string passphrase,
        Argon2Parameters parameters,
        byte[] salt,
        byte[] nonce)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(salt);
        ArgumentNullException.ThrowIfNull(nonce);
        ArgumentException.ThrowIfNullOrEmpty(passphrase);
        ValidateParameters(parameters);
        ArgumentOutOfRangeException.ThrowIfNotEqual(salt.Length, SaltBytes);
        ArgumentOutOfRangeException.ThrowIfNotEqual(nonce.Length, NonceBytes);

        var problems = ExportBundleWriter.Verify(bundle);
        if (problems.Count > 0)
        {
            throw new InvalidOperationException("Bundle failed verification: " + string.Join("; ", problems));
        }

        var plaintext = SerializeFiles(bundle.Files);
        var output = new byte[_headerBytes + plaintext.Length];
        var header = output.AsSpan(0, _headerBytes);
        Magic.CopyTo(header);
        header[_magicBytes] = FormatVersion;
        header[_kdfIdOffset] = KdfArgon2id;
        header[_kdfParametersVersionOffset] = KdfParametersVersion;
        BinaryPrimitives.WriteUInt32BigEndian(header.Slice(_memoryOffset, 4), (uint)parameters.MemoryKiB);
        BinaryPrimitives.WriteUInt32BigEndian(header.Slice(_timeCostOffset, 4), (uint)parameters.TimeCost);
        header[_parallelismOffset] = (byte)parameters.Parallelism;
        salt.CopyTo(header.Slice(_saltOffset, SaltBytes));
        nonce.CopyTo(header.Slice(_nonceOffset, NonceBytes));

        var key = DeriveKey(passphrase, salt, parameters);
        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Encrypt(
                nonce,
                plaintext,
                output.AsSpan(_headerBytes),
                header.Slice(_tagOffset, TagBytes),
                header[.._aadBytes]);
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
        if (archive.Length <= _headerBytes)
        {
            throw new InvalidDataException("Not an encrypted export bundle, or it is truncated.");
        }

        if (!archive[.._magicBytes].SequenceEqual(Magic))
        {
            throw new InvalidDataException("Not an encrypted export bundle: magic does not match.");
        }

        if (archive[_magicBytes] != FormatVersion)
        {
            throw new InvalidDataException("Unsupported export bundle format version: " + archive[_magicBytes]);
        }

        if (archive[_kdfIdOffset] != KdfArgon2id)
        {
            throw new InvalidDataException("Unsupported export bundle key derivation.");
        }

        if (archive[_kdfParametersVersionOffset] != KdfParametersVersion)
        {
            throw new InvalidDataException("Unsupported export bundle key derivation parameters.");
        }

        var parameters = new Argon2Parameters(
            (int)BinaryPrimitives.ReadUInt32BigEndian(archive.Slice(_memoryOffset, 4)),
            (int)BinaryPrimitives.ReadUInt32BigEndian(archive.Slice(_timeCostOffset, 4)),
            archive[_parallelismOffset]);
        if (!IsValid(parameters))
        {
            throw new InvalidDataException("Export bundle key derivation parameters are outside the accepted range.");
        }

        var salt = archive.Slice(_saltOffset, SaltBytes).ToArray();
        var nonce = archive.Slice(_nonceOffset, NonceBytes).ToArray();
        var tag = archive.Slice(_tagOffset, TagBytes).ToArray();
        var ciphertext = archive[_headerBytes..].ToArray();
        var aad = archive[.._aadBytes].ToArray();

        var plaintext = new byte[ciphertext.Length];
        var key = DeriveKey(passphrase, salt, parameters);
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

    private static bool IsValid(Argon2Parameters parameters) =>
        parameters.MemoryKiB is >= MinMemoryKiB and <= MaxMemoryKiB
        && parameters.TimeCost is >= MinTimeCost and <= MaxTimeCost
        && parameters.Parallelism is >= 1 and <= MaxParallelism
        && parameters.MemoryKiB >= 8 * parameters.Parallelism;

    private static void ValidateParameters(Argon2Parameters parameters)
    {
        if (!IsValid(parameters))
        {
            throw new ArgumentOutOfRangeException(
                nameof(parameters),
                $"Memory must be {MinMemoryKiB}-{MaxMemoryKiB} KiB, time cost {MinTimeCost}-{MaxTimeCost}, " +
                $"parallelism 1-{MaxParallelism} with at least 8 KiB of memory per lane.");
        }
    }

    private static byte[] DeriveKey(string passphrase, byte[] salt, Argon2Parameters parameters)
    {
        var passphraseBytes = Encoding.UTF8.GetBytes(passphrase);
        try
        {
            using var argon2 = new Argon2id(passphraseBytes)
            {
                Salt = salt,
                DegreeOfParallelism = parameters.Parallelism,
                Iterations = parameters.TimeCost,
                MemorySize = parameters.MemoryKiB,
            };
            return argon2.GetBytes(_keyBytes);
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
            if (count < 0 || count > _maxFileCount)
            {
                throw new InvalidDataException("Export bundle file count is out of range.");
            }

            var files = new List<BundleFile>(count);
            for (var i = 0; i < count; i++)
            {
                var pathLength = reader.ReadInt32();
                if (pathLength <= 0 || pathLength > _maxPathBytes)
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
