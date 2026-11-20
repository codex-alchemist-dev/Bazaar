using System.Security.Cryptography;
using System.Text;
using Bazaar.Kodo.Runtime;

namespace Bazaar.Kodo.Tests.Runtime;

/// <summary>
/// Test-only signing helpers. Keys are generated at runtime and never written into the repository.
/// </summary>
internal static class RuntimeTestSigning
{
    /// <summary>The fixed "now" every test uses, so validity windows are deterministic.</summary>
    public static readonly DateTimeOffset Now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The key id the test trust root pins.</summary>
    public const string KeyId = "release-test";

    private static readonly Lazy<RSA> _primary = new(() => RSA.Create(2048));
    private static readonly Lazy<RSA> _other = new(() => RSA.Create(2048));

    /// <summary>The key the test trust root trusts.</summary>
    public static RSA PrimaryKey => _primary.Value;

    /// <summary>A second key that is not trusted under <see cref="KeyId"/>.</summary>
    public static RSA OtherKey => _other.Value;

    /// <summary>A trust root that pins <paramref name="key"/> (default: the primary key) under <paramref name="keyId"/>.</summary>
    public static RuntimeTrustRoot TrustRoot(
        RSA? key = null,
        DateTimeOffset? notBefore = null,
        DateTimeOffset? notAfter = null,
        string keyId = KeyId) =>
        new([
            new TrustedKey(
                keyId,
                RsaPkcs1Sha256Verifier.AlgorithmName,
                (key ?? PrimaryKey).ExportSubjectPublicKeyInfo(),
                notBefore ?? Now.AddDays(-1),
                notAfter ?? Now.AddYears(1)),
        ]);

    /// <summary>The base64 RSA PKCS#1 SHA-256 signature of <paramref name="message"/>.</summary>
    public static string Sign(RSA key, ReadOnlySpan<byte> message) =>
        Convert.ToBase64String(key.SignData(message, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));

    /// <summary>A small slot with three files, used by most tests.</summary>
    public static Dictionary<string, string> SampleContents() => new(StringComparer.Ordinal)
    {
        ["kodo.dll"] = "binary-a",
        ["probes/catalogue.json"] = "{}",
        ["runbooks/restart.md"] = "# restart",
    };

    /// <summary>The UTF-8 bytes of <paramref name="text"/>.</summary>
    public static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>The files as the verifier takes them: path to bytes.</summary>
    public static Dictionary<string, ReadOnlyMemory<byte>> ToSlotFiles(IReadOnlyDictionary<string, string> contents)
    {
        Dictionary<string, ReadOnlyMemory<byte>> files = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> entry in contents)
        {
            files[entry.Key] = Bytes(entry.Value);
        }

        return files;
    }

    /// <summary>A manifest that lists exactly <paramref name="contents"/>, sorted by path, with correct hashes and sizes.</summary>
    public static RuntimeManifest ManifestFor(string slotId, IReadOnlyDictionary<string, string> contents)
    {
        List<RuntimeFileEntry> entries = [];
        foreach (string path in contents.Keys.OrderBy(p => p, StringComparer.Ordinal))
        {
            byte[] bytes = Bytes(contents[path]);
            entries.Add(new RuntimeFileEntry(path, RuntimeManifestCodec.Sha256Hex(bytes), bytes.Length));
        }

        return new RuntimeManifest("0.1.0", slotId, "build-test-1", entries);
    }

    /// <summary>The stored envelope for <paramref name="manifest"/>, signed by <paramref name="key"/> under <paramref name="keyId"/>.</summary>
    public static byte[] SignedEnvelope(RuntimeManifest manifest, RSA? key = null, string keyId = KeyId)
    {
        string signature = Sign(key ?? PrimaryKey, RuntimeManifestCodec.CanonicalizeManifest(manifest));
        return RuntimeManifestCodec.EncodeEnvelope(new SignedRuntimeManifest(manifest, keyId, signature));
    }

    /// <summary>The stored pointer that selects <paramref name="slotId"/> and names the digest of <paramref name="envelope"/>.</summary>
    public static byte[] SignedPointer(string slotId, byte[] envelope, RSA? key = null, string keyId = KeyId)
    {
        SlotPointerBody body = new(slotId, RuntimeManifestCodec.Sha256Hex(envelope));
        string signature = Sign(key ?? PrimaryKey, RuntimeManifestCodec.CanonicalizePointerBody(body));
        return RuntimeManifestCodec.EncodePointer(new SlotPointer(body.SlotId, body.ManifestSha256, keyId, signature));
    }
}
