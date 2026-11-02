using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bazaar.Contracts.Soul;
using Bazaar.Server.Souls.Import;

namespace Bazaar.Server.Tests.Souls.Import;

/// <summary>
/// Test-only signature verifier. It uses HMAC-SHA256 with keys held in the test, so it is a stand-in for the real
/// verifier and is never registered in the server.
/// </summary>
internal sealed class FakeBundleSignatureVerifier(IReadOnlyDictionary<string, byte[]> keys) : IBundleSignatureVerifier
{
    public const string Algorithm = "FAKE-HMAC-SHA256";

    public BundleSignatureStatus Verify(ReadOnlySpan<byte> manifestBytes, BundleSignature signature)
    {
        if (!keys.TryGetValue(signature.KeyId, out var key))
            return BundleSignatureStatus.UnknownKey;
        if (signature.Algorithm != Algorithm)
            return BundleSignatureStatus.Invalid;
        byte[] actual;
        try
        {
            actual = Convert.FromBase64String(signature.SignatureBase64);
        }
        catch (FormatException)
        {
            return BundleSignatureStatus.Invalid;
        }
        var expected = HMACSHA256.HashData(key, manifestBytes);
        return CryptographicOperations.FixedTimeEquals(expected, actual)
            ? BundleSignatureStatus.Valid
            : BundleSignatureStatus.Invalid;
    }

    /// <summary>Signs manifest bytes with a test key.</summary>
    public static BundleSignature Sign(string keyId, byte[] key, byte[] manifestBytes) =>
        new(keyId, Algorithm, Convert.ToBase64String(HMACSHA256.HashData(key, manifestBytes)));
}

/// <summary>Builds a bundle (files, manifest, signature) for import tests.</summary>
internal sealed class BundleBuilder
{
    public const string KeyId = "test-key-1";

    public static readonly byte[] Key = Encoding.UTF8.GetBytes("test-only-key-material-not-a-secret");

    public static readonly Guid SoulId = Guid.Parse("2b4f7c1e-8a1d-4a6e-9c3b-5d7e9f101112");

    private readonly List<BundleFileContent> _files = [];
    private readonly List<BundleFile> _entries = [];

    /// <summary>The verifier that trusts <see cref="KeyId"/>.</summary>
    public static FakeBundleSignatureVerifier Verifier { get; } =
        new(new Dictionary<string, byte[]> { [KeyId] = Key });

    /// <summary>The files as they would be received.</summary>
    public IReadOnlyList<BundleFileContent> Files => _files;

    /// <summary>Adds a file and a matching, correct manifest entry.</summary>
    public BundleBuilder AddFile(string path, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        _files.Add(new BundleFileContent(path, bytes));
        _entries.Add(new BundleFile(path, Sha256Hex(bytes), bytes.Length));
        return this;
    }

    /// <summary>Adds a file the manifest does not list.</summary>
    public BundleBuilder AddUnlistedFile(string path, string text)
    {
        _files.Add(new BundleFileContent(path, Encoding.UTF8.GetBytes(text)));
        return this;
    }

    /// <summary>Adds a manifest entry with no file.</summary>
    public BundleBuilder AddEntryOnly(string path, string sha256, long size)
    {
        _entries.Add(new BundleFile(path, sha256, size));
        return this;
    }

    /// <summary>Adds a file with the given bytes, but an entry built from other bytes (for digest tests).</summary>
    public BundleBuilder AddFileWithEntry(string path, string receivedText, BundleFile entry)
    {
        _files.Add(new BundleFileContent(path, Encoding.UTF8.GetBytes(receivedText)));
        _entries.Add(entry);
        return this;
    }

    /// <summary>Serializes a well-formed manifest over the entries added so far.</summary>
    public byte[] ManifestBytes() => JsonSerializer.SerializeToUtf8Bytes(
        new BundleManifest(
            FormatVersion: BundleImporter.SupportedFormatVersion,
            SoulId: SoulId,
            SoulVersion: 3,
            ContentSha256: Sha256Hex(Encoding.UTF8.GetBytes("content")),
            Exporter: "test-exporter",
            ExportedAt: DateTimeOffset.Parse("2026-10-30T12:00:00+00:00", System.Globalization.CultureInfo.InvariantCulture),
            Files: _entries.ToList(),
            Exclusions: ["secrets", "personal-fields"]),
        BundleManifestJsonContext.Default.BundleManifest);

    /// <summary>Signs the manifest bytes with the test key.</summary>
    public static BundleSignature SignManifest(byte[] manifestBytes) =>
        FakeBundleSignatureVerifier.Sign(KeyId, Key, manifestBytes);

    /// <summary>Returns the manifest bytes and a correct signature for the entries added so far.</summary>
    public (byte[] Manifest, BundleSignature Signature) Sealed()
    {
        var manifest = ManifestBytes();
        return (manifest, SignManifest(manifest));
    }

    public static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
