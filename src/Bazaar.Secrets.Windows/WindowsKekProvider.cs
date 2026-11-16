using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace Bazaar.Secrets.Windows;

/// <summary>Where the KEK's private key lives. Reported so operators can see the custody level they actually got.</summary>
public enum WindowsKekCustody
{
    /// <summary>The key lives in the TPM (Microsoft Platform Crypto Provider) and never leaves it.</summary>
    Tpm,

    /// <summary>The key lives in the Microsoft Software Key Storage Provider, protected by Windows (DPAPI-NG class of protection).</summary>
    Software,
}

/// <summary>
/// An <see cref="IKekProvider"/> backed by a non-exportable CNG RSA key. The key is created in the TPM when a
/// TPM-backed provider is available, and in the Microsoft Software Key Storage Provider otherwise. The provider
/// is one selectable custody option; the owner's Windows custody choice is still parked (docs 14 and 33).
/// </summary>
/// <remarks>
/// Wrap format (version 1): a fresh 256-bit data key seals each wrapped key with AES-256-GCM, bound to the
/// caller's associated data. The data key is then RSA-OAEP(SHA-256) wrapped under the CNG key. This keeps
/// the associated data out of the RSA block, so its size does not limit it.
/// Layout: magic "BKW1" | u16 wrapped-data-key length | wrapped data key | 12-byte nonce | 16-byte tag | ciphertext.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsKekProvider : IKekProvider, IDisposable
{
    private const int _rsaBits = 2048;
    private const int _dataKeyBytes = 32;
    private const int _nonceBytes = 12;
    private const int _tagBytes = 16;
    private static readonly byte[] _magic = "BKW1"u8.ToArray();
    private readonly ConcurrentDictionary<string, RSA> _keys = new(StringComparer.Ordinal);
    private readonly object _createLock = new();
    private readonly string _providerName;
    private bool _disposed;

    /// <summary>
    /// Opens or creates the named KEK. With <paramref name="custody"/> null the TPM is tried first and the software
    /// provider is the fallback. An explicit choice never falls back: it either works or throws.
    /// </summary>
    public WindowsKekProvider(string keyName, WindowsKekCustody? custody = null, WindowsKekScope scope = WindowsKekScope.User)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyName);
        KeyName = keyName;
        _scope = scope;

        if (custody is WindowsKekCustody.Tpm)
        {
            Custody = WindowsKekCustody.Tpm;
            _providerName = CngProvider.MicrosoftPlatformCryptoProvider.Provider;
            EnsureKey(keyName, _providerName, _scope);
            return;
        }

        if (custody is WindowsKekCustody.Software)
        {
            Custody = WindowsKekCustody.Software;
            _providerName = CngProvider.MicrosoftSoftwareKeyStorageProvider.Provider;
            EnsureKey(keyName, _providerName, _scope);
            return;
        }

        // Automatic: prefer the TPM. A machine without a usable TPM refuses key creation, and then the software provider takes over.
        try
        {
            EnsureKey(keyName, CngProvider.MicrosoftPlatformCryptoProvider.Provider, _scope);
            Custody = WindowsKekCustody.Tpm;
            _providerName = CngProvider.MicrosoftPlatformCryptoProvider.Provider;
        }
        catch (CryptographicException)
        {
            EnsureKey(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider.Provider, _scope);
            Custody = WindowsKekCustody.Software;
            _providerName = CngProvider.MicrosoftSoftwareKeyStorageProvider.Provider;
        }
    }

    private readonly WindowsKekScope _scope;

    /// <summary>Where the key is stored: the signed-in user's store (the desktop client), or the machine store (the server service, which needs admin to create the key).</summary>
    public enum WindowsKekScope
    {
        /// <summary>The current user's key store. No administrator rights are needed. The default for the desktop client.</summary>
        User,

        /// <summary>The machine key store, for a service account. Creating the key needs administrator rights.</summary>
        Machine,
    }

    /// <summary>The name of the current CNG key. New wraps use it; old wraps keep the name they were sealed under.</summary>
    public string KeyName { get; }

    /// <summary>The custody level actually in use.</summary>
    public WindowsKekCustody Custody { get; }

    /// <inheritdoc />
    public string CurrentKeyId => KeyName;

    /// <inheritdoc />
    public Task<ReadOnlyMemory<byte>> WrapAsync(ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> associatedData, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        var kek = OpenKey(CurrentKeyId);
        var dataKey = RandomNumberGenerator.GetBytes(_dataKeyBytes);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(_nonceBytes);
            var ciphertext = new byte[key.Length];
            var tag = new byte[_tagBytes];
            using (var aes = new AesGcm(dataKey, _tagBytes))
            {
                aes.Encrypt(nonce, key.Span, ciphertext, tag, associatedData.Span);
            }

            var wrappedDataKey = kek.Encrypt(dataKey, RSAEncryptionPadding.OaepSHA256);
            return Task.FromResult<ReadOnlyMemory<byte>>(Pack(wrappedDataKey, nonce, tag, ciphertext));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    /// <inheritdoc />
    public Task<ReadOnlyMemory<byte>> UnwrapAsync(string keyId, ReadOnlyMemory<byte> wrapped, ReadOnlyMemory<byte> associatedData, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);

        var kek = OpenKey(keyId);
        var (wrappedDataKey, nonce, tag, ciphertext) = Unpack(wrapped.Span);

        byte[] dataKey;
        try
        {
            dataKey = kek.Decrypt(wrappedDataKey, RSAEncryptionPadding.OaepSHA256);
        }
        catch (CryptographicException)
        {
            throw new CryptographicException("The wrapped key could not be opened under this KEK.");
        }

        try
        {
            if (dataKey.Length != _dataKeyBytes)
            {
                throw new CryptographicException("The wrapped key could not be opened under this KEK.");
            }

            var plaintext = new byte[ciphertext.Length];
            try
            {
                using var aes = new AesGcm(dataKey, _tagBytes);
                aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData.Span);
            }
            catch (CryptographicException)
            {
                CryptographicOperations.ZeroMemory(plaintext);
                throw new CryptographicException("The wrapped key failed authentication. The associated data does not match.");
            }

            return Task.FromResult<ReadOnlyMemory<byte>>(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    /// <summary>Releases the handles to the CNG keys. The keys themselves stay in the store.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var key in _keys.Values)
        {
            key.Dispose();
        }

        _keys.Clear();
    }

    private static CngKeyOpenOptions OpenOptionsFor(WindowsKekScope scope) =>
        scope == WindowsKekScope.Machine ? CngKeyOpenOptions.MachineKey : CngKeyOpenOptions.None;

    private static void EnsureKey(string keyName, string provider, WindowsKekScope scope)
    {
        var cngProvider = new CngProvider(provider);
        var openOptions = OpenOptionsFor(scope);
        if (CngKey.Exists(keyName, cngProvider, openOptions))
        {
            return;
        }

        var parameters = new CngKeyCreationParameters
        {
            Provider = cngProvider,
            KeyUsage = CngKeyUsages.Decryption,
            KeyCreationOptions = scope == WindowsKekScope.Machine ? CngKeyCreationOptions.MachineKey : CngKeyCreationOptions.None,
            UIPolicy = new CngUIPolicy(CngUIProtectionLevels.None),
        };
        parameters.Parameters.Add(new CngProperty("Length", BitConverter.GetBytes(_rsaBits), CngPropertyOptions.None));

        using var created = CngKey.Create(CngAlgorithm.Rsa, keyName, parameters);
    }

    private RSA OpenKey(string keyName)
    {
        if (_keys.TryGetValue(keyName, out var cached))
        {
            return cached;
        }

        lock (_createLock)
        {
            if (_keys.TryGetValue(keyName, out cached))
            {
                return cached;
            }

            var cngProvider = new CngProvider(_providerName);
            var openOptions = OpenOptionsFor(_scope);
            if (!CngKey.Exists(keyName, cngProvider, openOptions))
            {
                throw new CryptographicException("The KEK named in the wrapped key is not present in this store.");
            }

            var key = CngKey.Open(keyName, cngProvider, openOptions);
            var rsa = new RSACng(key);
            _keys[keyName] = rsa;
            return rsa;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static ReadOnlyMemory<byte> Pack(byte[] wrappedDataKey, byte[] nonce, byte[] tag, byte[] ciphertext)
    {
        var total = _magic.Length + 2 + wrappedDataKey.Length + nonce.Length + tag.Length + ciphertext.Length;
        var buffer = new byte[total];
        var offset = 0;

        _magic.CopyTo(buffer, offset);
        offset += _magic.Length;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset, 2), checked((ushort)wrappedDataKey.Length));
        offset += 2;
        wrappedDataKey.CopyTo(buffer, offset);
        offset += wrappedDataKey.Length;
        nonce.CopyTo(buffer, offset);
        offset += nonce.Length;
        tag.CopyTo(buffer, offset);
        offset += tag.Length;
        ciphertext.CopyTo(buffer, offset);
        return buffer;
    }

    private static (byte[] WrappedDataKey, byte[] Nonce, byte[] Tag, byte[] Ciphertext) Unpack(ReadOnlySpan<byte> wrapped)
    {
        var header = _magic.Length + 2;
        if (wrapped.Length < header + _nonceBytes + _tagBytes || !wrapped[.._magic.Length].SequenceEqual(_magic))
        {
            throw new CryptographicException("The wrapped key is not in a recognised format.");
        }

        var wrappedLength = BinaryPrimitives.ReadUInt16BigEndian(wrapped.Slice(_magic.Length, 2));
        var offset = header;
        if (wrapped.Length < offset + wrappedLength + _nonceBytes + _tagBytes)
        {
            throw new CryptographicException("The wrapped key is too short to be valid.");
        }

        var wrappedDataKey = wrapped.Slice(offset, wrappedLength).ToArray();
        offset += wrappedLength;
        var nonce = wrapped.Slice(offset, _nonceBytes).ToArray();
        offset += _nonceBytes;
        var tag = wrapped.Slice(offset, _tagBytes).ToArray();
        offset += _tagBytes;
        var ciphertext = wrapped[offset..].ToArray();
        return (wrappedDataKey, nonce, tag, ciphertext);
    }
}
