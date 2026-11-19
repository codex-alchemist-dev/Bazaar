using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Bazaar.Server.Components.Backup.Recovery;

/// <summary>
/// Seals and unseals the recovery bundle (doc 48 section 6). Layout, all integers big endian:
/// <c>magic "BZRB" (4) | format version (1) | KDF id (1) | iterations (4) | salt (16) | nonce (12) | tag (16) | ciphertext</c>.
/// The first 38 bytes (everything before the tag) are the AES-GCM associated data, so any header edit fails
/// authentication. The key is PBKDF2-HMAC-SHA256 of the passphrase, 32 bytes.
/// <para>
/// Hook for a second key source: the hardware-key and quorum parts are OPEN (doc 48 section 7). They would add a
/// further input to the key derivation under a new KDF id; ids 2 and above are reserved for that.
/// </para>
/// </summary>
public static class RecoveryBundleSealer
{
    /// <summary>The only bundle format version this build writes or reads.</summary>
    public const byte FormatVersion = 1;

    /// <summary>KDF id for PBKDF2-HMAC-SHA256, the only KDF in version 1.</summary>
    public const byte KdfPbkdf2HmacSha256 = 1;

    /// <summary>The largest bundle <see cref="Unseal"/> accepts. Larger input is rejected before any allocation or key derivation.</summary>
    public const int MaxBundleBytes = 64 * 1024 * 1024;

    private const int _keySize = 32;
    private const int _saltSize = 16;
    private const int _nonceSize = 12;
    private const int _tagSize = 16;
    private const int _magicSize = 4;
    private const int _iterationsOffset = _magicSize + 2;
    private const int _saltOffset = _iterationsOffset + 4;
    private const int _nonceOffset = _saltOffset + _saltSize;
    private const int _tagOffset = _nonceOffset + _nonceSize;
    private const int _headerSize = _tagOffset + _tagSize;

    // Everything before the tag. Bound to the ciphertext as associated data.
    private const int _aadSize = _tagOffset;

    private static readonly byte[] _magic = "BZRB"u8.ToArray();

    /// <summary>Seals a payload under a key derived from the passphrase. Each call uses a fresh salt and nonce.</summary>
    /// <exception cref="ArgumentException">The passphrase is empty, or the payload version is not current.</exception>
    public static byte[] Seal(RecoveryBundlePayload payload, string passphrase, KdfOptions kdf)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(kdf);
        ArgumentException.ThrowIfNullOrEmpty(passphrase);
        if (payload.Version != RecoveryBundlePayload.CurrentVersion)
        {
            throw new ArgumentException("Payload version is not the current recovery bundle payload version.", nameof(payload));
        }

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(payload, RecoveryBundleJsonContext.Default.RecoveryBundlePayload);
        var output = new byte[_headerSize + plaintext.Length];
        _magic.CopyTo(output, 0);
        output[_magicSize] = FormatVersion;
        output[_magicSize + 1] = KdfPbkdf2HmacSha256;
        BinaryPrimitives.WriteInt32BigEndian(output.AsSpan(_iterationsOffset, 4), kdf.Iterations);
        RandomNumberGenerator.Fill(output.AsSpan(_saltOffset, _saltSize));
        RandomNumberGenerator.Fill(output.AsSpan(_nonceOffset, _nonceSize));

        var key = DeriveKey(passphrase, output.AsSpan(_saltOffset, _saltSize), kdf.Iterations);
        try
        {
            using var aes = new AesGcm(key, _tagSize);
            aes.Encrypt(
                output.AsSpan(_nonceOffset, _nonceSize),
                plaintext,
                output.AsSpan(_headerSize),
                output.AsSpan(_tagOffset, _tagSize),
                output.AsSpan(0, _aadSize));
            return output;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>Unseals a bundle. Every header field and the ciphertext are checked; any alteration fails.</summary>
    /// <remarks>
    /// The returned payload holds secret material and is owned by the caller, who must dispose it.
    /// </remarks>
    /// <exception cref="RecoveryBundleException">The bundle is invalid, altered, too large, or the passphrase is wrong.</exception>
    /// <exception cref="ArgumentException">The passphrase is empty.</exception>
    public static RecoveryBundlePayload Unseal(byte[] bundle, string passphrase)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentException.ThrowIfNullOrEmpty(passphrase);

        if (bundle.Length > MaxBundleBytes)
        {
            throw new RecoveryBundleException(RecoveryBundleFailure.TooLarge, "Recovery bundle is larger than the supported maximum.");
        }

        if (bundle.Length < _headerSize)
        {
            throw new RecoveryBundleException(RecoveryBundleFailure.Truncated, "Recovery bundle is shorter than its header.");
        }

        if (!bundle.AsSpan(0, _magicSize).SequenceEqual(_magic))
        {
            throw new RecoveryBundleException(RecoveryBundleFailure.BadMagic, "File is not a recovery bundle.");
        }

        if (bundle[_magicSize] != FormatVersion)
        {
            throw new RecoveryBundleException(RecoveryBundleFailure.UnknownVersion, "Recovery bundle format version is not supported.");
        }

        if (bundle[_magicSize + 1] != KdfPbkdf2HmacSha256)
        {
            throw new RecoveryBundleException(RecoveryBundleFailure.UnknownKdf, "Recovery bundle names an unknown key derivation function.");
        }

        var iterations = BinaryPrimitives.ReadInt32BigEndian(bundle.AsSpan(_iterationsOffset, 4));
        if (iterations < KdfOptions.MinimumIterations || iterations > KdfOptions.MaximumIterations)
        {
            throw new RecoveryBundleException(RecoveryBundleFailure.InvalidHeader, "Recovery bundle KDF iteration count is out of range.");
        }

        var ciphertextLength = bundle.Length - _headerSize;
        var plaintext = new byte[ciphertextLength];
        var key = DeriveKey(passphrase, bundle.AsSpan(_saltOffset, _saltSize), iterations);
        try
        {
            using var aes = new AesGcm(key, _tagSize);
            try
            {
                aes.Decrypt(
                    bundle.AsSpan(_nonceOffset, _nonceSize),
                    bundle.AsSpan(_headerSize, ciphertextLength),
                    bundle.AsSpan(_tagOffset, _tagSize),
                    plaintext,
                    bundle.AsSpan(0, _aadSize));
            }
            catch (CryptographicException)
            {
                throw new RecoveryBundleException(
                    RecoveryBundleFailure.Tampered,
                    "Recovery bundle could not be authenticated: the passphrase is wrong or the file was altered.");
            }

            return ParsePayload(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static RecoveryBundlePayload ParsePayload(byte[] plaintext)
    {
        RecoveryBundlePayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize(plaintext, RecoveryBundleJsonContext.Default.RecoveryBundlePayload);
        }
        catch (JsonException)
        {
            throw new RecoveryBundleException(RecoveryBundleFailure.InvalidPayload, "Recovery bundle payload is not valid.");
        }

        if (payload is null)
        {
            throw new RecoveryBundleException(RecoveryBundleFailure.InvalidPayload, "Recovery bundle payload is empty.");
        }

        if (payload.Version != RecoveryBundlePayload.CurrentVersion)
        {
            payload.Dispose();
            throw new RecoveryBundleException(RecoveryBundleFailure.UnknownVersion, "Recovery bundle payload version is not supported.");
        }

        return payload;
    }

    private static byte[] DeriveKey(string passphrase, ReadOnlySpan<byte> salt, int iterations)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(passphrase);
        try
        {
            return Rfc2898DeriveBytes.Pbkdf2(passwordBytes, salt, iterations, HashAlgorithmName.SHA256, _keySize);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }
}
