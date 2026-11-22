using System.Security.Cryptography;
using Bazaar.Client.SignIn;

namespace Bazaar.Client.Linux.Secrets;

/// <summary>Where the session key lives. Reported to the person so the file fallback is never silent.</summary>
public enum SessionKeyCustody
{
    /// <summary>The key is in the desktop's Secret Service keyring, reached through <c>secret-tool</c>.</summary>
    Libsecret,

    /// <summary>The key is in a separate mode 0600 file beside the session (no keyring was available).</summary>
    File,
}

/// <summary>
/// Seals the saved session with AES-256-GCM for this Linux user (doc 45 section 2.6, doc 30). The sealed bytes are
/// <c>version, nonce, tag, ciphertext</c>; the key is held by the keyring (libsecret) or, when none is available, by a
/// separate 0600 file. A wrong key, a short or altered blob all read as "cannot open" (null), never as an exception, so the
/// session store signs the person out instead of failing.
/// </summary>
public sealed class LinuxSecretProtector : ISecretProtector
{
    private const byte _version = 1;
    private const int _nonceSize = 12;
    private const int _tagSize = 16;
    private const int _keySize = 32;

    // Binds the sealed bytes to this purpose, so another blob sealed under the same key cannot be swapped in.
    private static readonly byte[] _associatedData = "bazaar.client.session.v1"u8.ToArray();

    private readonly byte[] _key;

    /// <summary>Creates a protector over a 32-byte key.</summary>
    public LinuxSecretProtector(byte[] key, SessionKeyCustody custody)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != _keySize) throw new ArgumentException("The session key must be 32 bytes.", nameof(key));
        _key = (byte[])key.Clone();
        Custody = custody;
    }

    /// <summary>Where the key this protector uses is held.</summary>
    public SessionKeyCustody Custody { get; }

    /// <summary>
    /// Picks the key custody: the keyring through <paramref name="runner"/> when it works, else a 0600 key file in
    /// <paramref name="stateDirectory"/>. Returns null when neither can hold a key (the caller then keeps the session in memory only).
    /// </summary>
    public static LinuxSecretProtector? TryOpen(ISecretToolRunner runner, string stateDirectory)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);

        var key = SecretToolKeyVault.TryGetOrCreate(runner);
        if (key is not null) return new LinuxSecretProtector(key, SessionKeyCustody.Libsecret);

        key = FileKeyVault.TryGetOrCreate(Path.Combine(stateDirectory, "session.key"));
        return key is null ? null : new LinuxSecretProtector(key, SessionKeyCustody.File);
    }

    /// <inheritdoc />
    public byte[] Protect(ReadOnlySpan<byte> plain)
    {
        var output = new byte[1 + _nonceSize + _tagSize + plain.Length];
        output[0] = _version;
        var nonce = output.AsSpan(1, _nonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_key, _tagSize);
        aes.Encrypt(nonce, plain, output.AsSpan(1 + _nonceSize + _tagSize), output.AsSpan(1 + _nonceSize, _tagSize), _associatedData);
        return output;
    }

    /// <inheritdoc />
    public byte[]? Unprotect(ReadOnlySpan<byte> sealedBytes)
    {
        if (sealedBytes.Length < 1 + _nonceSize + _tagSize || sealedBytes[0] != _version) return null;

        var plain = new byte[sealedBytes.Length - 1 - _nonceSize - _tagSize];
        try
        {
            using var aes = new AesGcm(_key, _tagSize);
            aes.Decrypt(
                sealedBytes.Slice(1, _nonceSize),
                sealedBytes[(1 + _nonceSize + _tagSize)..],
                sealedBytes.Slice(1 + _nonceSize, _tagSize),
                plain,
                _associatedData);
            return plain;
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(plain);
            return null;
        }
    }
}
