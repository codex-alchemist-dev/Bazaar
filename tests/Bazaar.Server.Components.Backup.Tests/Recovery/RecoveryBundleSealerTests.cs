using System.Buffers.Binary;
using Bazaar.Server.Components.Backup.Recovery;

namespace Bazaar.Server.Components.Backup.Tests.Recovery;

// Each Seal or successful Unseal runs PBKDF2 at the 600000 floor, so tests keep the number of such calls small.
public sealed class RecoveryBundleSealerTests
{
    private const string _passphrase = "correct horse battery staple";

    // Header offsets, from the layout in RecoveryBundleSealer.
    private const int _versionOffset = 4;
    private const int _kdfOffset = 5;
    private const int _iterationsOffset = 6;
    private const int _saltOffset = 10;
    private const int _nonceOffset = 26;
    private const int _headerSize = 54;

    private static RecoveryBundlePayload SamplePayload(int version = RecoveryBundlePayload.CurrentVersion) =>
        new(
            version,
            new Dictionary<string, string> { ["store"] = "ref-1" },
            [1, 2, 3],
            [4, 5],
            [new RecoveryBackupSetEntry("base-1", new string('a', 64), 1024)],
            [new RecoveryModuleEntry("mod.one", "1.2.3", "ab12")],
            [6]);

    private static byte[] Flip(byte[] source, int index)
    {
        var copy = (byte[])source.Clone();
        copy[index] ^= 0x01;
        return copy;
    }

    [Fact]
    public void Round_trip_restores_every_field()
    {
        var payload = SamplePayload();

        var bundle = RecoveryBundleSealer.Seal(payload, _passphrase, KdfOptions.Default);
        var restored = RecoveryBundleSealer.Unseal(bundle, _passphrase);

        Assert.Equal(RecoveryBundlePayload.CurrentVersion, restored.Version);
        Assert.Equal("ref-1", restored.BootstrapReferences["store"]);
        Assert.Equal([1, 2, 3], restored.KekExport);
        Assert.Equal([4, 5], restored.AuditCheckpointHead);
        Assert.Equal("base-1", Assert.Single(restored.BackupSets).Name);
        Assert.Equal(1024, Assert.Single(restored.BackupSets).SizeBytes);
        Assert.Equal("1.2.3", Assert.Single(restored.Modules).Version);
        Assert.Equal("ab12", Assert.Single(restored.Modules).SignatureHex);
        Assert.Equal([6], restored.OwnerRecoveryMaterial);
    }

    [Fact]
    public void Wrong_passphrase_is_reported_as_Tampered()
    {
        var bundle = RecoveryBundleSealer.Seal(SamplePayload(), _passphrase, KdfOptions.Default);

        var ex = Assert.Throws<RecoveryBundleException>(() => RecoveryBundleSealer.Unseal(bundle, "wrong passphrase"));

        Assert.Equal(RecoveryBundleFailure.Tampered, ex.Failure);
        Assert.DoesNotContain(_passphrase, ex.Message);
        Assert.DoesNotContain("wrong passphrase", ex.Message);
    }

    [Fact]
    public void Every_single_byte_flip_fails()
    {
        var bundle = RecoveryBundleSealer.Seal(SamplePayload(), _passphrase, KdfOptions.Default);

        var survivors = new List<int>();
        for (var i = 0; i < bundle.Length; i++)
        {
            try
            {
                RecoveryBundleSealer.Unseal(Flip(bundle, i), _passphrase);
                survivors.Add(i);
            }
            catch (RecoveryBundleException)
            {
                // Expected: every alteration is rejected with a typed failure.
            }
        }

        Assert.Empty(survivors);
    }

    [Fact]
    public void Truncation_before_the_end_of_the_header_is_Truncated()
    {
        var bundle = RecoveryBundleSealer.Seal(SamplePayload(), _passphrase, KdfOptions.Default);

        for (var length = 0; length < _headerSize; length++)
        {
            var ex = Assert.Throws<RecoveryBundleException>(() => RecoveryBundleSealer.Unseal(bundle[..length], _passphrase));
            Assert.Equal(RecoveryBundleFailure.Truncated, ex.Failure);
        }
    }

    [Fact]
    public void Truncation_inside_the_ciphertext_fails_authentication()
    {
        var bundle = RecoveryBundleSealer.Seal(SamplePayload(), _passphrase, KdfOptions.Default);

        var ex = Assert.Throws<RecoveryBundleException>(() => RecoveryBundleSealer.Unseal(bundle[..^1], _passphrase));

        Assert.Equal(RecoveryBundleFailure.Tampered, ex.Failure);
    }

    [Fact]
    public void Unknown_format_version_is_rejected_before_any_key_work()
    {
        var bundle = RecoveryBundleSealer.Seal(SamplePayload(), _passphrase, KdfOptions.Default);
        var altered = (byte[])bundle.Clone();
        altered[_versionOffset] = 2;

        var ex = Assert.Throws<RecoveryBundleException>(() => RecoveryBundleSealer.Unseal(altered, _passphrase));

        Assert.Equal(RecoveryBundleFailure.UnknownVersion, ex.Failure);
    }

    [Fact]
    public void Bad_magic_and_unknown_kdf_have_their_own_failures()
    {
        var bundle = RecoveryBundleSealer.Seal(SamplePayload(), _passphrase, KdfOptions.Default);
        var badMagic = Flip(bundle, 0);
        var badKdf = (byte[])bundle.Clone();
        badKdf[_kdfOffset] = 9;

        Assert.Equal(RecoveryBundleFailure.BadMagic, Assert.Throws<RecoveryBundleException>(() => RecoveryBundleSealer.Unseal(badMagic, _passphrase)).Failure);
        Assert.Equal(RecoveryBundleFailure.UnknownKdf, Assert.Throws<RecoveryBundleException>(() => RecoveryBundleSealer.Unseal(badKdf, _passphrase)).Failure);
    }

    [Theory]
    [InlineData(599_999)]
    [InlineData(10_000_001)]
    [InlineData(int.MaxValue)]
    public void Header_iteration_count_outside_the_floor_and_ceiling_is_rejected_before_derivation(int iterations)
    {
        var bundle = RecoveryBundleSealer.Seal(SamplePayload(), _passphrase, KdfOptions.Default);
        var altered = (byte[])bundle.Clone();
        BinaryPrimitives.WriteInt32BigEndian(altered.AsSpan(_iterationsOffset, 4), iterations);

        var ex = Assert.Throws<RecoveryBundleException>(() => RecoveryBundleSealer.Unseal(altered, _passphrase));

        Assert.Equal(RecoveryBundleFailure.InvalidHeader, ex.Failure);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(599_999)]
    [InlineData(10_000_001)]
    public void KdfOptions_rejects_counts_outside_the_floor_and_ceiling(int iterations)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new KdfOptions(iterations));
    }

    [Fact]
    public void KdfOptions_accepts_the_floor_the_ceiling_and_defaults_to_the_floor()
    {
        Assert.Equal(600_000, KdfOptions.Default.Iterations);
        Assert.Equal(KdfOptions.MinimumIterations, new KdfOptions(KdfOptions.MinimumIterations).Iterations);
        Assert.Equal(KdfOptions.MaximumIterations, new KdfOptions(KdfOptions.MaximumIterations).Iterations);
    }

    [Fact]
    public void Two_seals_of_the_same_payload_differ_in_salt_and_nonce()
    {
        var payload = SamplePayload();

        var first = RecoveryBundleSealer.Seal(payload, _passphrase, KdfOptions.Default);
        var second = RecoveryBundleSealer.Seal(payload, _passphrase, KdfOptions.Default);

        Assert.NotEqual(first, second);
        Assert.NotEqual(first.AsSpan(_saltOffset, 16).ToArray(), second.AsSpan(_saltOffset, 16).ToArray());
        Assert.NotEqual(first.AsSpan(_nonceOffset, 12).ToArray(), second.AsSpan(_nonceOffset, 12).ToArray());
    }

    [Fact]
    public void Seal_rejects_an_empty_passphrase_and_a_non_current_payload_version()
    {
        Assert.Throws<ArgumentException>(() => RecoveryBundleSealer.Seal(SamplePayload(), "", KdfOptions.Default));
        Assert.Throws<ArgumentException>(() => RecoveryBundleSealer.Seal(SamplePayload(version: 2), _passphrase, KdfOptions.Default));
    }
}
