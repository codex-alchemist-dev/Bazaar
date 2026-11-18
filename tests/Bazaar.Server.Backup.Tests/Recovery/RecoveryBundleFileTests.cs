using Bazaar.Server.Backup.Recovery;

namespace Bazaar.Server.Backup.Tests.Recovery;

// Each Write or successful Read runs PBKDF2 at the 600000 floor, so tests keep the number of such calls small.
public sealed class RecoveryBundleFileTests : IDisposable
{
    private const string _passphrase = "correct horse battery staple";
    private const string _wrongPassphrase = "not the passphrase";

    private readonly TempDir _dir = new();

    public RecoveryBundleFileTests() => Directory.CreateDirectory(_dir.Path);

    public void Dispose() => _dir.Dispose();

    private string BundlePath => Path.Combine(_dir.Path, "bundle.bzrb");

    private static RecoveryBundlePayload SamplePayload(string store = "ref-1") =>
        new(
            RecoveryBundlePayload.CurrentVersion,
            new Dictionary<string, string> { ["store"] = store },
            [1, 2, 3],
            [4, 5],
            [new RecoveryBackupSetEntry("base-1", new string('a', 64), 1024)],
            [new RecoveryModuleEntry("mod.one", "1.2.3", "ab12")],
            [6]);

    [Fact]
    public void Round_trip_write_then_read_returns_the_same_payload()
    {
        RecoveryBundleFile.Write(BundlePath, SamplePayload(), _passphrase, KdfOptions.Default);

        var result = RecoveryBundleFile.Read(BundlePath, _passphrase);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Failure);
        using var payload = result.Payload!;
        Assert.Equal(RecoveryBundlePayload.CurrentVersion, payload.Version);
        Assert.Equal("ref-1", payload.BootstrapReferences["store"]);
        Assert.Equal([1, 2, 3], payload.KekExport);
        Assert.Equal([4, 5], payload.AuditCheckpointHead);
        var set = Assert.Single(payload.BackupSets);
        Assert.Equal("base-1", set.Name);
        Assert.Equal(new string('a', 64), set.Sha256Hex);
        Assert.Equal(1024, set.SizeBytes);
        var module = Assert.Single(payload.Modules);
        Assert.Equal("mod.one", module.Id);
        Assert.Equal("1.2.3", module.Version);
        Assert.Equal("ab12", module.SignatureHex);
        Assert.Equal([6], payload.OwnerRecoveryMaterial);
    }

    [Fact]
    public void Wrong_passphrase_fails_cleanly_without_secret_detail()
    {
        RecoveryBundleFile.Write(BundlePath, SamplePayload(), _passphrase, KdfOptions.Default);

        var result = RecoveryBundleFile.Read(BundlePath, _wrongPassphrase);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Payload);
        Assert.Equal(RecoveryBundleFailure.Tampered, result.Failure);
        Assert.NotNull(result.Message);
        Assert.DoesNotContain(_passphrase, result.Message);
        Assert.DoesNotContain(_wrongPassphrase, result.Message);
    }

    [Fact]
    public void Tampered_byte_in_the_ciphertext_fails_authentication()
    {
        RecoveryBundleFile.Write(BundlePath, SamplePayload(), _passphrase, KdfOptions.Default);
        var bytes = File.ReadAllBytes(BundlePath);
        bytes[^1] ^= 0x01;
        File.WriteAllBytes(BundlePath, bytes);

        var result = RecoveryBundleFile.Read(BundlePath, _passphrase);

        Assert.False(result.IsSuccess);
        Assert.Equal(RecoveryBundleFailure.Tampered, result.Failure);
    }

    [Fact]
    public void Unknown_format_version_is_refused()
    {
        RecoveryBundleFile.Write(BundlePath, SamplePayload(), _passphrase, KdfOptions.Default);
        var bytes = File.ReadAllBytes(BundlePath);
        bytes[4] = RecoveryBundleSealer.FormatVersion + 1;
        File.WriteAllBytes(BundlePath, bytes);

        var result = RecoveryBundleFile.Read(BundlePath, _passphrase);

        Assert.False(result.IsSuccess);
        Assert.Equal(RecoveryBundleFailure.UnknownVersion, result.Failure);
    }

    [Fact]
    public void File_over_the_size_cap_is_refused_before_reading()
    {
        // A sparse file one byte over the cap. It is not a valid bundle, so a read that reached Unseal would fail differently.
        using (var stream = new FileStream(BundlePath, FileMode.CreateNew, FileAccess.Write))
        {
            stream.SetLength(RecoveryBundleSealer.MaxBundleBytes + 1);
        }

        var result = RecoveryBundleFile.Read(BundlePath, _passphrase);

        Assert.False(result.IsSuccess);
        Assert.Equal(RecoveryBundleFailure.TooLarge, result.Failure);
    }

    [Fact]
    public void Existing_target_is_not_overwritten_without_the_flag()
    {
        RecoveryBundleFile.Write(BundlePath, SamplePayload("original"), _passphrase, KdfOptions.Default);
        var before = File.ReadAllBytes(BundlePath);

        Assert.Throws<IOException>(() =>
            RecoveryBundleFile.Write(BundlePath, SamplePayload("replacement"), _passphrase, KdfOptions.Default));

        Assert.Equal(before, File.ReadAllBytes(BundlePath));
        Assert.Empty(TempFilesIn(_dir.Path));
    }

    [Fact]
    public void Existing_target_is_replaced_when_the_overwrite_flag_is_passed()
    {
        RecoveryBundleFile.Write(BundlePath, SamplePayload("original"), _passphrase, KdfOptions.Default);

        RecoveryBundleFile.Write(BundlePath, SamplePayload("replacement"), _passphrase, KdfOptions.Default, overwrite: true);

        using var payload = RecoveryBundleFile.Read(BundlePath, _passphrase).Payload!;
        Assert.Equal("replacement", payload.BootstrapReferences["store"]);
        Assert.Empty(TempFilesIn(_dir.Path));
    }

    [Fact]
    public void Temporary_file_is_not_left_behind_after_a_failed_write()
    {
        // A directory at the target path passes the existence check (File.Exists is false for directories), so the
        // final rename fails after the temporary file has been written and flushed.
        Directory.CreateDirectory(BundlePath);

        Assert.ThrowsAny<IOException>(() =>
            RecoveryBundleFile.Write(BundlePath, SamplePayload(), _passphrase, KdfOptions.Default, overwrite: true));

        Assert.Empty(TempFilesIn(_dir.Path));
    }

    [Fact]
    public void Reading_a_missing_file_throws_file_not_found()
    {
        Assert.Throws<FileNotFoundException>(() => RecoveryBundleFile.Read(BundlePath, _passphrase));
    }

    private static string[] TempFilesIn(string directory) =>
        Directory.GetFiles(directory, "*.tmp", SearchOption.TopDirectoryOnly);
}
