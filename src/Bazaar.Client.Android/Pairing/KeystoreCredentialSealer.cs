using Android.Runtime;
using Android.Security.Keystore;
using Bazaar.Client.Core.Pairing;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;

namespace Bazaar.Client.Android.Pairing;

/// <summary>
/// AES-256-GCM under a non-exportable key in the Android Keystore (doc 32, section 5; doc 29). The key is created on first
/// use and stays in the Keystore. The output is the 12-byte IV followed by the ciphertext and its 128-bit tag.
/// </summary>
/// <remarks>
/// The refresh credential is not bound to user authentication: it is used in the background by the refresh flow. The
/// approval signing key (doc 32, section 5a) is a separate key and does require a fresh biometric prompt.
/// </remarks>
public sealed class KeystoreCredentialSealer(string alias) : ICredentialSealer
{
    private const string Provider = "AndroidKeyStore";
    private const string Transformation = "AES/GCM/NoPadding";
    private const int TagBits = 128;
    private const int IvBytes = 12;

    private readonly string _alias = !string.IsNullOrWhiteSpace(alias)
        ? alias
        : throw new ArgumentException("A Keystore alias is required.", nameof(alias));

    public byte[] Seal(byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        using var cipher = Cipher.GetInstance(Transformation)
            ?? throw new InvalidOperationException("AES/GCM is not available.");
        cipher.Init(CipherMode.EncryptMode, GetOrCreateKey(_alias));
        var iv = cipher.GetIV() ?? throw new InvalidOperationException("The Keystore cipher returned no IV.");
        var ciphertext = cipher.DoFinal(plaintext) ?? throw new InvalidOperationException("The Keystore cipher returned no output.");

        var sealedBytes = new byte[iv.Length + ciphertext.Length];
        iv.CopyTo(sealedBytes, 0);
        ciphertext.CopyTo(sealedBytes, iv.Length);
        return sealedBytes;
    }

    public byte[]? Open(byte[] sealedBytes)
    {
        ArgumentNullException.ThrowIfNull(sealedBytes);
        if (sealedBytes.Length <= IvBytes)
        {
            return null;
        }

        var key = FindKey(_alias);
        if (key is null)
        {
            return null;
        }

        try
        {
            using var cipher = Cipher.GetInstance(Transformation)
                ?? throw new InvalidOperationException("AES/GCM is not available.");
            cipher.Init(CipherMode.DecryptMode, key, new GCMParameterSpec(TagBits, sealedBytes[..IvBytes]));
            return cipher.DoFinal(sealedBytes[IvBytes..]);
        }
        catch (GeneralSecurityException)
        {
            // A bad tag, or a key the Keystore has invalidated. The caller treats this as unrecoverable.
            return null;
        }
    }

    private static ISecretKey? FindKey(string alias)
    {
        using var store = KeyStore.GetInstance(Provider)
            ?? throw new InvalidOperationException("The Android Keystore is not available.");
        store.Load(null);
        return store.GetKey(alias, null)?.JavaCast<ISecretKey>();
    }

    private static ISecretKey GetOrCreateKey(string alias)
    {
        var existing = FindKey(alias);
        if (existing is not null)
        {
            return existing;
        }

        using var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, Provider)
            ?? throw new InvalidOperationException("AES key generation is not available.");
        var spec = new KeyGenParameterSpec.Builder(alias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetBlockModes(KeyProperties.BlockModeGcm)
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)
            .SetKeySize(256)
            .SetRandomizedEncryptionRequired(true)
            .Build();
        generator.Init(spec);
        return generator.GenerateKey() ?? throw new InvalidOperationException("The Keystore returned no key.");
    }
}
