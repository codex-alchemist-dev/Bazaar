using System.Security.Cryptography;
using Android.Security.Keystore;
using Bazaar.Client.Approvals;
using Java.Security;
using Java.Security.Spec;

namespace Bazaar.Client.Android.Approvals;

/// <summary>
/// Signs approval payloads with an ECDSA P-256 key held in the Android Keystore (doc 29 Approvals, doc 32 section 5a). The
/// key never leaves the secure hardware. The Keystore returns DER; the approval message uses 64-byte P1363, so the result is
/// converted (<see cref="EcdsaSignatureFormat"/>). Enrolment is separate (doc 32 Question 4 is open): signing never creates
/// a key, so a device with no enrolled key fails closed. UNVERIFIED on device: this head cannot be built in CI.
/// </summary>
public sealed class KeystoreDeviceApprovalSigner(string deviceId) : IDeviceApprovalSigner
{
    /// <summary>Keystore alias of the approval key.</summary>
    public const string KeyAlias = "bazaar.approval.key";

    private const string Provider = "AndroidKeyStore";

    public string DeviceId { get; } = deviceId;

    /// <summary>True when the approval key exists on this device.</summary>
    public static bool HasKey()
    {
        var store = KeyStore.GetInstance(Provider)!;
        store.Load(null);
        return store.ContainsAlias(KeyAlias);
    }

    /// <summary>
    /// Creates the approval key if there is none and returns its public key (X.509 SubjectPublicKeyInfo) for enrolment. Called by
    /// the enrolment flow only, never by the signer.
    /// </summary>
    public static byte[] CreateKeyForEnrolment()
    {
        var store = KeyStore.GetInstance(Provider)!;
        store.Load(null);
        if (!store.ContainsAlias(KeyAlias))
        {
            var generator = KeyPairGenerator.GetInstance(KeyProperties.KeyAlgorithmEc, Provider)!;
            var spec = new KeyGenParameterSpec.Builder(KeyAlias, KeyStorePurpose.Sign)
                .SetAlgorithmParameterSpec(new ECGenParameterSpec("secp256r1"))!
                .SetDigests(KeyProperties.DigestSha256)!
                .Build();
            generator.Initialize(spec);
            generator.GenerateKeyPair();
        }

        return store.GetCertificate(KeyAlias)!.PublicKey!.GetEncoded()!;
    }

    public ValueTask<byte[]> SignAsync(ReadOnlyMemory<byte> canonicalPayload, CancellationToken cancellationToken)
    {
        var store = KeyStore.GetInstance(Provider)!;
        store.Load(null);
        if (!store.ContainsAlias(KeyAlias) || store.GetKey(KeyAlias, null) is not IPrivateKey key)
        {
            throw new CryptographicException("No approval key is enrolled on this device.");
        }

        var signature = Signature.GetInstance("SHA256withECDSA")!;
        signature.InitSign(key);
        signature.Update(canonicalPayload.ToArray());
        return ValueTask.FromResult(EcdsaSignatureFormat.DerToP1363(signature.Sign()!));
    }
}
