using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Portability.Tests;

public class ExportBundleCryptoTests
{
    private const string _passphrase = "correct horse battery staple";

    // Header offsets from ExportBundleCipher: memory 7..11, time cost 11..15, parallelism 15, salt 16..32, nonce 32..44, tag 44..60.
    private const int _kdfIdOffset = 5;
    private const int _memoryOffset = 7;
    private const int _timeCostOffset = 11;
    private const int _parallelismOffset = 15;
    private const int _saltOffset = 16;
    private const int _nonceOffset = 32;
    private const int _tagOffset = 44;
    private const int _ciphertextOffset = 60;

    private static readonly DateTimeOffset _created = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);

    // Archive made by an independent Python reference, not by this code: the Argon2id key from argon2-cffi (the PHC C
    // implementation) and AES-256-GCM from the cryptography package, over the same files as VectorBundle(). Parameters:
    // memory 65536 KiB, time cost 3, parallelism 4, salt 0x10..0x1F, nonce 0xA0..0xAB, passphrase "vector-passphrase".
    // The test checks that this archive decrypts, which proves the key and the layout agree with the reference.
    // Regenerate only if the format changes.
    private static readonly byte[] _vectorArchive =
    [
        0x42, 0x5A, 0x45, 0x58, 0x02, 0x01, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0x04,
        0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F,
        0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6, 0xA7, 0xA8, 0xA9, 0xAA, 0xAB, 0x22, 0x4E, 0x6C, 0xD7,
        0x81, 0x66, 0x1B, 0xF9, 0x04, 0xAE, 0xC1, 0x5D, 0x04, 0xA5, 0x23, 0xC3, 0xB0, 0x4A, 0xCE, 0xF1,
        0x95, 0xDF, 0xA0, 0x0D, 0x9B, 0xD3, 0x54, 0xDC, 0x89, 0xE1, 0x63, 0xC6, 0xFB, 0xDF, 0x64, 0xBD,
        0x1D, 0x64, 0x61, 0xFF, 0xF2, 0x46, 0x1E, 0xC7, 0xF0, 0x92, 0x37, 0xA7, 0x97, 0x56, 0x7B, 0xBD,
        0x96, 0x16, 0x46, 0x9D, 0x6A, 0x5C, 0xF1, 0x86, 0x42, 0xE9, 0xCC, 0x8E, 0xD2, 0x71, 0xF0, 0x10,
        0x79, 0x47, 0xE8, 0xE0, 0xCD, 0x29, 0xBF, 0x46, 0xE9, 0x77, 0x4C, 0x94, 0x0A, 0x15, 0x93, 0x8F,
        0xC3, 0x9E, 0x83, 0xBC, 0xEB, 0x87, 0xDE, 0xA5, 0xE8, 0x5D, 0x9E, 0x48, 0xEE, 0x6A, 0xE9, 0x21,
        0x43, 0x01, 0x7F, 0x09, 0x0A, 0xB9, 0xFF, 0x36, 0x9C, 0x21, 0x40, 0xA7, 0x45, 0x30, 0xD0, 0x20,
        0xD9, 0x25, 0x33, 0xE0, 0xB9, 0xB8, 0xB0, 0xAB, 0xC0, 0x2A, 0xFF, 0xE3, 0x11, 0x87, 0xC0, 0x50,
        0x8A, 0xA8, 0xB6, 0x0F, 0xA5, 0xD2, 0x88, 0x90, 0x74, 0x1D, 0x61, 0xDE, 0x87, 0x75, 0x41, 0x92,
        0x8F, 0xEA, 0x32, 0x43, 0xB3, 0x31, 0xBC, 0xBE, 0xF1, 0x83, 0xA1, 0x31, 0x05, 0xA4, 0xE4, 0x80,
        0xB6, 0x4C, 0xE2, 0x8C, 0x29, 0xBB, 0xDA, 0x4A, 0x0E, 0x92, 0x1E, 0xFD, 0xA5, 0x9D, 0x27, 0x4E,
        0xEC, 0x36, 0x3E, 0xCB, 0x87, 0x5A, 0x13, 0xB1, 0x1E, 0x99, 0x94, 0x77, 0x3F, 0x43, 0x65, 0x69,
        0x0F, 0x69, 0x4B, 0x43, 0x2D, 0x09, 0xD0, 0xF4, 0x9A, 0xC2, 0x3D, 0xB0, 0x4A, 0xBB, 0xF0, 0x2C,
        0xED, 0x1C, 0xA5, 0xF2, 0x98, 0x8F, 0xED, 0x43, 0xE5, 0x6F, 0xAF, 0xCC, 0x28, 0x69, 0xE3, 0xA9,
        0x52, 0xAD, 0x14, 0x77, 0x4A, 0xFC, 0xE1, 0xAC, 0xC0, 0x37, 0x5F, 0xBA, 0x89, 0x6B, 0xCE, 0xCB,
        0xD7, 0x87, 0xCD, 0x60, 0x70, 0x57, 0x0E, 0xDA, 0x2C, 0xBB, 0x77, 0x5F, 0x7D, 0x01, 0xDF, 0xB3,
        0x5F, 0x7E, 0x3B, 0x99, 0xB9, 0x58, 0xC6, 0xB4, 0xB0, 0xDB, 0xAC, 0xC2, 0xAC, 0xA8, 0x1D, 0x80,
        0xF7, 0x04, 0xCB, 0xED, 0x69, 0xE7, 0xB8, 0x2A, 0x8F, 0xA9, 0xFE, 0xF9, 0x4E, 0x50, 0x6A, 0xB3,
        0x43, 0x20, 0xE4, 0x24, 0xED, 0x24, 0xBC, 0xDF, 0x19, 0x7B, 0xDD, 0xBD, 0x0A, 0xE5, 0x14, 0xA9,
        0x77, 0x7B, 0xF8, 0xEA, 0x61, 0xE1, 0x6E, 0x4B, 0x1D,
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
    public void Header_records_the_argon2id_parameters_and_version()
    {
        var archive = ExportBundleCipher.Encrypt(SampleBundle(), _passphrase);

        Assert.Equal(ExportBundleCipher.FormatVersion, archive[4]);
        Assert.Equal(ExportBundleCipher.KdfArgon2id, archive[_kdfIdOffset]);
        Assert.Equal(ExportBundleCipher.KdfParametersVersion, archive[6]);
        Assert.Equal(65_536u, BinaryPrimitives.ReadUInt32BigEndian(archive.AsSpan(_memoryOffset, 4)));
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32BigEndian(archive.AsSpan(_timeCostOffset, 4)));
        Assert.Equal(4, archive[_parallelismOffset]);
    }

    [Fact]
    public void Custom_parameters_are_written_to_the_header_and_read_back()
    {
        var parameters = new Argon2Parameters(ExportBundleCipher.MinMemoryKiB, ExportBundleCipher.MinTimeCost, 1);
        var bundle = SampleBundle();

        var archive = ExportBundleCipher.Encrypt(bundle, _passphrase, parameters);

        Assert.Equal((uint)ExportBundleCipher.MinMemoryKiB, BinaryPrimitives.ReadUInt32BigEndian(archive.AsSpan(_memoryOffset, 4)));
        Assert.Equal((uint)ExportBundleCipher.MinTimeCost, BinaryPrimitives.ReadUInt32BigEndian(archive.AsSpan(_timeCostOffset, 4)));
        Assert.Equal(1, archive[_parallelismOffset]);
        AssertSameBundle(bundle, ExportBundleCipher.Decrypt(archive, _passphrase));
    }

    [Theory]
    [InlineData(ExportBundleCipher.MinMemoryKiB - 1, 3, 4)]
    [InlineData(ExportBundleCipher.MaxMemoryKiB + 1, 3, 4)]
    [InlineData(65_536, ExportBundleCipher.MinTimeCost - 1, 4)]
    [InlineData(65_536, 3, 0)]
    [InlineData(65_536, 3, ExportBundleCipher.MaxParallelism + 1)]
    [InlineData(32, 3, 4)]
    public void Parameters_outside_the_accepted_range_are_refused_on_encrypt(int memory, int time, int parallelism)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ExportBundleCipher.Encrypt(SampleBundle(), _passphrase, new Argon2Parameters(memory, time, parallelism)));
    }

    [Fact]
    public void Memory_below_the_floor_is_refused_before_key_derivation()
    {
        var archive = ExportBundleCipher.Encrypt(SampleBundle(), _passphrase);
        BinaryPrimitives.WriteUInt32BigEndian(archive.AsSpan(_memoryOffset, 4), 1_000);

        Assert.Throws<InvalidDataException>(() => ExportBundleCipher.Decrypt(archive, _passphrase));
    }

    [Fact]
    public void Memory_above_the_ceiling_is_refused_before_key_derivation()
    {
        var archive = ExportBundleCipher.Encrypt(SampleBundle(), _passphrase);
        BinaryPrimitives.WriteUInt32BigEndian(archive.AsSpan(_memoryOffset, 4), (uint)ExportBundleCipher.MaxMemoryKiB + 1);

        Assert.Throws<InvalidDataException>(() => ExportBundleCipher.Decrypt(archive, _passphrase));
    }

    [Fact]
    public void Unknown_kdf_id_is_refused()
    {
        var archive = ExportBundleCipher.Encrypt(SampleBundle(), _passphrase);
        archive[_kdfIdOffset] = 2;

        Assert.Throws<InvalidDataException>(() => ExportBundleCipher.Decrypt(archive, _passphrase));
    }

    [Fact]
    public void Unknown_kdf_parameter_version_is_refused()
    {
        var archive = ExportBundleCipher.Encrypt(SampleBundle(), _passphrase);
        archive[6] = 2;

        Assert.Throws<InvalidDataException>(() => ExportBundleCipher.Decrypt(archive, _passphrase));
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
    public void Changed_time_cost_fails_because_the_header_is_authenticated()
    {
        var archive = ExportBundleCipher.Encrypt(SampleBundle(), _passphrase);
        BinaryPrimitives.WriteUInt32BigEndian(archive.AsSpan(_timeCostOffset, 4), 4);

        Assert.Throws<CryptographicException>(() => ExportBundleCipher.Decrypt(archive, _passphrase));
    }

    [Fact]
    public void Changed_memory_cost_fails_because_the_header_is_authenticated()
    {
        var archive = ExportBundleCipher.Encrypt(SampleBundle(), _passphrase);
        BinaryPrimitives.WriteUInt32BigEndian(archive.AsSpan(_memoryOffset, 4), 65_537);

        Assert.Throws<CryptographicException>(() => ExportBundleCipher.Decrypt(archive, _passphrase));
    }

    [Fact]
    public void Time_cost_below_the_floor_is_refused_before_key_derivation()
    {
        var archive = ExportBundleCipher.Encrypt(SampleBundle(), _passphrase);
        BinaryPrimitives.WriteUInt32BigEndian(archive.AsSpan(_timeCostOffset, 4), 1);

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
    public void Checked_in_test_vector_from_an_independent_argon2_reference_decrypts()
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
