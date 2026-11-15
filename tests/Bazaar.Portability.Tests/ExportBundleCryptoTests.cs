using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Portability.Tests;

public class ExportBundleCryptoTests
{
    private const string _passphrase = "correct horse battery staple";

    // Header offsets from ExportBundleCipher: iterations 6..10, salt 10..26, nonce 26..38, tag 38..54.
    private const int _iterationsOffset = 6;
    private const int _saltOffset = 10;
    private const int _nonceOffset = 26;
    private const int _tagOffset = 38;
    private const int _ciphertextOffset = 54;

    private static readonly DateTimeOffset _created = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);

    // Archive made once by ExportBundleCipher.EncryptWithParameters from VectorBundle() with the fixed salt
    // 0x10..0x1F, the fixed nonce 0xA0..0xAB, 600000 iterations and passphrase "vector-passphrase".
    // Regenerate only if the format changes; the test checks that old archives still decrypt.
    private static readonly byte[] _vectorArchive =
    [
        0x42, 0x5A, 0x45, 0x58, 0x01, 0x01, 0x00, 0x09, 0x27, 0xC0, 0x10, 0x11, 0x12, 0x13, 0x14, 0x15,
        0x16, 0x17, 0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5,
        0xA6, 0xA7, 0xA8, 0xA9, 0xAA, 0xAB, 0xA1, 0x89, 0x28, 0x2D, 0x01, 0xDE, 0xCC, 0xDE, 0x89, 0xDC,
        0xB1, 0xAC, 0xEE, 0x2F, 0x98, 0x89, 0x33, 0xFD, 0xF2, 0xA4, 0x38, 0x03, 0xD0, 0x8B, 0xC4, 0x74,
        0x19, 0xD4, 0xDD, 0x4D, 0xAC, 0x9F, 0x95, 0x9C, 0xA3, 0x6C, 0x1F, 0xE5, 0xF7, 0x74, 0x6F, 0xCB,
        0xE6, 0xA5, 0xE9, 0xEB, 0xB2, 0xBB, 0x9F, 0x73, 0xB9, 0x60, 0x9D, 0xB4, 0x4F, 0xED, 0x7A, 0x2D,
        0x09, 0x54, 0x06, 0x66, 0x70, 0x15, 0x8E, 0x76, 0xB1, 0x39, 0x4A, 0xBA, 0x8D, 0x56, 0x57, 0x71,
        0xBD, 0xA1, 0x9E, 0x21, 0x7A, 0x57, 0xB1, 0xB7, 0xB9, 0x87, 0x68, 0x75, 0xBC, 0x13, 0x98, 0x45,
        0x99, 0xF1, 0xC1, 0x42, 0xB4, 0xA2, 0xA6, 0x76, 0xF8, 0x4C, 0x0F, 0x32, 0x57, 0x49, 0x9D, 0xF1,
        0x60, 0xBB, 0xB5, 0x4D, 0x02, 0x31, 0xD2, 0x27, 0x2E, 0x95, 0x38, 0xD1, 0xBD, 0x2C, 0xD3, 0x3E,
        0x60, 0xDF, 0xE5, 0xD8, 0x05, 0x1E, 0x7B, 0xDD, 0xCE, 0x3F, 0x63, 0xD8, 0xD0, 0x53, 0x9F, 0x4A,
        0x12, 0xD1, 0x73, 0xF5, 0xDC, 0x15, 0x35, 0x26, 0xB1, 0xBE, 0x12, 0xFE, 0x17, 0x0D, 0x94, 0x6C,
        0x10, 0x20, 0xCD, 0xF7, 0xC4, 0xD7, 0xE4, 0x2F, 0x94, 0xAC, 0x6A, 0xA5, 0x52, 0x7B, 0x89, 0x46,
        0x3C, 0x88, 0x34, 0xA9, 0x33, 0x06, 0x84, 0x69, 0xEB, 0x8F, 0x42, 0xFB, 0x38, 0xD8, 0x3E, 0x40,
        0xC1, 0xAB, 0x88, 0xC6, 0x98, 0x6A, 0xA6, 0x39, 0xD2, 0x60, 0x5C, 0x8E, 0x23, 0x15, 0xA6, 0xB1,
        0xA1, 0x25, 0x0F, 0x3B, 0xB8, 0x93, 0xD9, 0x93, 0x1B, 0xCD, 0x25, 0x3A, 0x3B, 0x08, 0x04, 0xB3,
        0xA5, 0x06, 0xDD, 0x4E, 0x3F, 0x97, 0x1F, 0x96, 0x95, 0xEF, 0x5B, 0x98, 0x3F, 0xFE, 0x11, 0xCC,
        0x97, 0x35, 0xE7, 0xC4, 0x92, 0x43, 0xD3, 0xC2, 0x30, 0xD4, 0xCB, 0x56, 0xC0, 0x8F, 0x88, 0xD5,
        0x57, 0x81, 0x43, 0xE7, 0x07, 0x3F, 0x75, 0xD0, 0x60, 0x52, 0x01, 0x4E, 0x9E, 0xAD, 0x58, 0x4B,
        0x26, 0x44, 0x6D, 0xA5, 0x6B, 0x25, 0x2A, 0x29, 0xBF, 0xD3, 0x04, 0xF6, 0x0A, 0x1A, 0xEB, 0x27,
        0xC3, 0x6A, 0xF5, 0xBC, 0x30, 0xAC, 0x1C, 0xB4, 0x6A, 0x12, 0x07, 0x02, 0x94, 0xD1, 0xFC, 0x67,
        0x97, 0xF8, 0x9A, 0x33, 0x5D, 0xA8, 0x8C, 0x29, 0x7F, 0x24, 0x38, 0xD0, 0xDC, 0xBF, 0xDF, 0x77,
        0xC5, 0x35, 0x52,
    ];

    private static byte[] SampleBytes => Encoding.UTF8.GetBytes("# Notes\n");

    private static ExportBundle SampleBundle() => ExportBundleWriter.Build("inst-1", _created,
    [
        new BundleFile("gc/alpha/messages.jsonl", Encoding.UTF8.GetBytes("{\"n\":1}\n")),
        new BundleFile("vault/notes.md", SampleBytes),
    ]);

    private static ExportBundle VectorBundle() => ExportBundleWriter.Build("inst-vector", _created,
    [
        new BundleFile("notes/hello.md", Encoding.UTF8.GetBytes("# Hello\n")),
    ]);

    private static ExportBundle WithManifest(ExportBundle bundle, string manifest) => bundle with
    {
        Manifest = manifest,
        Files = bundle.Files
            .Select(f => f.Path == ExportBundleWriter.ManifestFileName ? f with { Content = Encoding.UTF8.GetBytes(manifest) } : f)
            .ToList(),
    };

    private static void AssertSameBundle(ExportBundle expected, ExportBundle actual)
    {
        Assert.Equal(expected.Manifest, actual.Manifest);
        Assert.Equal(expected.Files.Select(f => f.Path), actual.Files.Select(f => f.Path));
        for (var i = 0; i < expected.Files.Count; i++)
        {
            Assert.Equal(expected.Files[i].Content, actual.Files[i].Content);
        }
    }

    [Fact]
    public void Round_trip_restores_the_same_bundle()
    {
        var bundle = SampleBundle();

        var archive = ExportBundleCipher.Encrypt(bundle, _passphrase);
        var restored = ExportBundleCipher.Decrypt(archive, _passphrase);

        AssertSameBundle(bundle, restored);
        Assert.Empty(ExportBundleWriter.Verify(restored));
    }

    [Fact]
    public void Each_encryption_uses_a_fresh_salt_and_nonce()
    {
        var bundle = SampleBundle();

        var first = ExportBundleCipher.Encrypt(bundle, _passphrase);
        var second = ExportBundleCipher.Encrypt(bundle, _passphrase);

        Assert.NotEqual(first.AsSpan(_saltOffset, 16).ToArray(), second.AsSpan(_saltOffset, 16).ToArray());
        Assert.NotEqual(first.AsSpan(_nonceOffset, 12).ToArray(), second.AsSpan(_nonceOffset, 12).ToArray());
    }

    [Fact]
    public void Header_records_at_least_600000_iterations()
    {
        var archive = ExportBundleCipher.Encrypt(SampleBundle(), _passphrase);

        var iterations = BinaryPrimitives.ReadUInt32BigEndian(archive.AsSpan(_iterationsOffset, 4));
        Assert.True(iterations >= 600_000);
    }

    [Fact]
    public void Wrong_passphrase_fails()
    {
        var archive = ExportBundleCipher.Encrypt(SampleBundle(), _passphrase);

        Assert.Throws<CryptographicException>(() => ExportBundleCipher.Decrypt(archive, "wrong passphrase"));
    }

    [Fact]
    public void Empty_passphrase_is_refused()
    {
        Assert.ThrowsAny<ArgumentException>(() => ExportBundleCipher.Encrypt(SampleBundle(), string.Empty));
    }

    [Theory]
    [InlineData(_saltOffset)]
    [InlineData(_nonceOffset)]
    [InlineData(_tagOffset)]
    [InlineData(_ciphertextOffset)]
    [InlineData(-1)]
    public void Flipped_byte_fails(int offset)
    {
        var archive = ExportBundleCipher.Encrypt(SampleBundle(), _passphrase);
        var index = offset < 0 ? archive.Length + offset : offset;
        archive[index] ^= 0x01;

        Assert.Throws<CryptographicException>(() => ExportBundleCipher.Decrypt(archive, _passphrase));
    }

    [Fact]
    public void Changed_iteration_count_fails_because_the_header_is_authenticated()
    {
        var archive = ExportBundleCipher.Encrypt(SampleBundle(), _passphrase);
        BinaryPrimitives.WriteUInt32BigEndian(archive.AsSpan(_iterationsOffset, 4), 600_001);

        Assert.Throws<CryptographicException>(() => ExportBundleCipher.Decrypt(archive, _passphrase));
    }

    [Fact]
    public void Iteration_count_below_the_floor_is_refused_before_key_derivation()
    {
        var archive = ExportBundleCipher.Encrypt(SampleBundle(), _passphrase);
        BinaryPrimitives.WriteUInt32BigEndian(archive.AsSpan(_iterationsOffset, 4), 1_000);

        Assert.Throws<InvalidDataException>(() => ExportBundleCipher.Decrypt(archive, _passphrase));
    }

    [Fact]
    public void Truncated_or_foreign_data_is_refused()
    {
        Assert.Throws<InvalidDataException>(() => ExportBundleCipher.Decrypt(new byte[10], _passphrase));

        var archive = ExportBundleCipher.Encrypt(SampleBundle(), _passphrase);
        archive[0] = (byte)'X';
        Assert.Throws<InvalidDataException>(() => ExportBundleCipher.Decrypt(archive, _passphrase));
    }

    [Fact]
    public void Checked_in_test_vector_decrypts()
    {
        var restored = ExportBundleCipher.Decrypt(_vectorArchive, "vector-passphrase");

        AssertSameBundle(VectorBundle(), restored);
        Assert.Empty(ExportBundleWriter.Verify(restored));
    }

    [Fact]
    public void Signature_verifies_with_the_public_key_only()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signed = BundleSigner.Sign(SampleBundle(), key);
        var publicKey = signed.Files.Single(f => f.Path == BundleSigner.PublicKeyFileName).Content;

        Assert.Empty(ExportBundleWriter.Verify(signed));
        Assert.True(BundleSigner.Verify(signed, publicKey));
    }

    [Fact]
    public void Signature_from_another_key_does_not_verify()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signed = BundleSigner.Sign(SampleBundle(), key);
        var otherPublic = other.ExportSubjectPublicKeyInfo();

        Assert.False(BundleSigner.Verify(signed, otherPublic));
    }

    [Fact]
    public void Changed_manifest_fails_verification()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signed = BundleSigner.Sign(SampleBundle(), key);
        var publicKey = signed.Files.Single(f => f.Path == BundleSigner.PublicKeyFileName).Content;

        var changedBoth = WithManifest(signed, signed.Manifest.Replace("inst-1", "inst-2", StringComparison.Ordinal));
        var changedFileOnly = signed with
        {
            Files = signed.Files
                .Select(f => f.Path == ExportBundleWriter.ManifestFileName ? f with { Content = Encoding.UTF8.GetBytes("{}") } : f)
                .ToList(),
        };

        Assert.False(BundleSigner.Verify(changedBoth, publicKey));
        Assert.False(BundleSigner.Verify(changedFileOnly, publicKey));
    }

    [Fact]
    public void Flipped_byte_in_the_signature_fails_verification()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signed = BundleSigner.Sign(SampleBundle(), key);
        var publicKey = signed.Files.Single(f => f.Path == BundleSigner.PublicKeyFileName).Content;
        var signature = (byte[])signed.Files.Single(f => f.Path == BundleSigner.SignatureFileName).Content.Clone();
        signature[0] ^= 0x01;
        var tampered = signed with
        {
            Files = signed.Files
                .Select(f => f.Path == BundleSigner.SignatureFileName ? f with { Content = signature } : f)
                .ToList(),
        };

        Assert.False(BundleSigner.Verify(tampered, publicKey));
    }

    [Fact]
    public void Signed_bundle_survives_encryption()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signed = BundleSigner.Sign(SampleBundle(), key);
        var publicKey = signed.Files.Single(f => f.Path == BundleSigner.PublicKeyFileName).Content;

        var archive = ExportBundleCipher.Encrypt(signed, _passphrase);
        var restored = ExportBundleCipher.Decrypt(archive, _passphrase);

        Assert.True(BundleSigner.Verify(restored, publicKey));
    }
}
