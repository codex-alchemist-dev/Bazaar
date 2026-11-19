using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Server.SealedStorage;

/// <summary>Byte layout and cryptography of doc 60 format v1. Everything here is AES-256-GCM with a fresh random 96-bit nonce per write.</summary>
internal static class SealFormat
{
    internal const byte Version = 1;
    internal const int NonceBytes = 12;
    internal const int TagBytes = 16;
    internal const int KeyBytes = 32;
    internal const int HeaderBytes = 4 + 1 + 8;
    internal const long MaxFileBytes = 256L * 1024 * 1024;
    internal const long MaxStoredBytes = MaxFileBytes + HeaderBytes + NonceBytes + TagBytes;
    internal const long MaxManifestBytes = 64L * 1024 * 1024;

    /// <summary>Writes a file and flushes it to the device before returning, so a later rename never exposes a short file after a power loss.</summary>
    internal static async ValueTask WriteDurableAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>Reads a whole file after checking its size, so a planted huge file cannot exhaust memory before authentication.</summary>
    internal static async ValueTask<byte[]?> ReadBoundedAsync(string path, long maxBytes, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > maxBytes)
        {
            return null;
        }

        return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
    }

    private static readonly byte[] _fileMagic = "BZSL"u8.ToArray();
    private static readonly byte[] _manifestMagic = "BZSM"u8.ToArray();
    private static readonly byte[] _keyMagic = "BZSK"u8.ToArray();

    internal static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    internal static byte[] DekAad(string scope, string kekId) => Aad("dek", Version, 0, scope, kekId);

    internal static byte[] FileAad(string scope, string path, long generation) => Aad("file", Version, generation, scope, path);

    internal static byte[] ManifestAad(string scope, long generation) => Aad("manifest", Version, generation, scope);

    /// <summary>Length-prefixed fields, so no choice of scope, path or key id can be re-split into another tuple.</summary>
    private static byte[] Aad(string purpose, byte version, long generation, params string[] fields)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, StrictUtf8, leaveOpen: true);
        writer.Write("bazaar.sealed.aad.v1"u8);
        writer.Write(version);
        writer.Write(generation);
        foreach (var field in new[] { purpose }.Concat(fields))
        {
            var bytes = StrictUtf8.GetBytes(field);
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        writer.Flush();
        return stream.ToArray();
    }

    internal static byte[] Derive(ReadOnlySpan<byte> dek, string purpose)
    {
        var output = new byte[KeyBytes];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, dek, output, salt: [], info: Encoding.UTF8.GetBytes($"bazaar.sealed.{purpose}.v1"));
        return output;
    }

    /// <summary>Rejects scope names that are empty, contain control characters or invalid UTF-16, and returns the NFC form.</summary>
    internal static string NormaliseScope(string scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        return Clean(scope, nameof(scope));
    }

    private static string Clean(string value, string parameter)
    {
        try
        {
            StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException)
        {
            throw new ArgumentException("Value contains invalid UTF-16 (a lone surrogate).", parameter);
        }

        if (value.Any(char.IsControl))
        {
            throw new ArgumentException("Value must not contain control characters.", parameter);
        }

        return value.Normalize(NormalizationForm.FormC);
    }

    /// <summary>Normalises a logical path: forward slashes, no empty, dot or dot-dot segments, no drive or leading slash. Throws <see cref="ArgumentException"/>.</summary>
    internal static string NormalisePath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var unified = Clean(path, nameof(path)).Replace('\\', '/');
        if (unified.Length == 0 || unified.Length > 1024 || unified.StartsWith('/') || unified.Contains(':'))
        {
            throw new ArgumentException("Sealed path must be relative, non-empty, at most 1024 characters, and contain no drive or NUL.", nameof(path));
        }

        foreach (var segment in unified.Split('/'))
        {
            if (segment.Length == 0 || segment == "." || segment == "..")
            {
                throw new ArgumentException("Sealed path segments must not be empty, '.' or '..'.", nameof(path));
            }
        }

        return unified;
    }

    internal static byte[] Seal(byte[] key, byte[] magic, long generation, ReadOnlySpan<byte> plaintext, byte[] aad)
    {
        var output = new byte[HeaderBytes + NonceBytes + plaintext.Length + TagBytes];
        magic.CopyTo(output, 0);
        output[4] = Version;
        BinaryPrimitives.WriteInt64BigEndian(output.AsSpan(5, 8), generation);
        var nonce = output.AsSpan(HeaderBytes, NonceBytes);
        RandomNumberGenerator.Fill(nonce);
        var cipher = output.AsSpan(HeaderBytes + NonceBytes, plaintext.Length);
        var tag = output.AsSpan(HeaderBytes + NonceBytes + plaintext.Length, TagBytes);
        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(nonce, plaintext, cipher, tag, aad);
        return output;
    }

    internal static byte[] SealFile(byte[] key, string scope, string path, long generation, ReadOnlySpan<byte> plaintext) =>
        Seal(key, _fileMagic, generation, plaintext, FileAad(scope, path, generation));

    internal static byte[] SealManifest(byte[] key, string scope, long generation, ReadOnlySpan<byte> plaintext) =>
        Seal(key, _manifestMagic, generation, plaintext, ManifestAad(scope, generation));

    /// <summary>Reads the generation from a sealed blob header without authenticating it. The value is authenticated by the AAD in <see cref="Open"/>.</summary>
    internal static bool TryReadHeader(ReadOnlySpan<byte> blob, byte[] magic, out long generation)
    {
        generation = 0;
        if (blob.Length < HeaderBytes + NonceBytes + TagBytes || !blob[..4].SequenceEqual(magic) || blob[4] != Version)
        {
            return false;
        }

        generation = BinaryPrimitives.ReadInt64BigEndian(blob.Slice(5, 8));
        return generation >= 0;
    }

    internal static byte[] FileMagic => _fileMagic;

    internal static byte[] ManifestMagic => _manifestMagic;

    internal static byte[] KeyMagic => _keyMagic;

    /// <summary>Authenticates and decrypts. Throws <see cref="CryptographicException"/> on any mismatch.</summary>
    internal static byte[] Open(byte[] key, ReadOnlySpan<byte> blob, byte[] aad)
    {
        var cipherLength = blob.Length - HeaderBytes - NonceBytes - TagBytes;
        var plaintext = new byte[cipherLength];
        using var aes = new AesGcm(key, TagBytes);
        aes.Decrypt(
            blob.Slice(HeaderBytes, NonceBytes),
            blob.Slice(HeaderBytes + NonceBytes, cipherLength),
            blob.Slice(HeaderBytes + NonceBytes + cipherLength, TagBytes),
            plaintext,
            aad);
        return plaintext;
    }

    internal static byte[] Sha256(ReadOnlySpan<byte> data) => SHA256.HashData(data);
}
