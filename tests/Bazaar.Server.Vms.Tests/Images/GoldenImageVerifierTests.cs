using Bazaar.Server.Vms.Images;

namespace Bazaar.Server.Vms.Tests.Images;

public sealed class GoldenImageVerifierTests
{
    private const string _requiredGuestAgent = "1.4.0";

    [Fact]
    public async Task Sealed_untouched_parent_verifies_ok()
    {
        using var dir = new TempImageDirectory();
        var (verifier, sealedParent) = await SealAsync(dir, "parent bytes");

        var result = await verifier.VerifyAsync(sealedParent, _requiredGuestAgent, TestContext.Current.CancellationToken);

        Assert.Equal(GoldenImageStatus.Ok, result.Status);
        Assert.True(result.IsOk);
        Assert.Contains("verified", result.Message);
    }

    [Fact]
    public async Task Missing_file_is_reported()
    {
        using var dir = new TempImageDirectory();
        var fs = new PhysicalImageFileSystem();
        var verifier = new GoldenImageVerifier(fs, new ImageHasher(fs));
        var record = new SealedImage(GoldenImageOs.Linux, 3, ImageTestData.EmptySha256, _requiredGuestAgent, dir.FilePath("gone.img"));

        var result = await verifier.VerifyAsync(record, _requiredGuestAgent, TestContext.Current.CancellationToken);

        Assert.Equal(GoldenImageStatus.Missing, result.Status);
        Assert.False(result.IsOk);
        Assert.Contains("missing", result.Message);
    }

    [Fact]
    public async Task Changed_bytes_are_a_hash_mismatch()
    {
        using var dir = new TempImageDirectory();
        var (verifier, sealedParent) = await SealAsync(dir, "original bytes");
        WriteTamperedBytes(sealedParent.Path, "tampered bytes");

        var result = await verifier.VerifyAsync(sealedParent, _requiredGuestAgent, TestContext.Current.CancellationToken);

        Assert.Equal(GoldenImageStatus.HashMismatch, result.Status);
        Assert.Contains(sealedParent.Sha256, result.Message);
    }

    [Fact]
    public async Task Writable_parent_is_reported()
    {
        using var dir = new TempImageDirectory();
        var (verifier, sealedParent) = await SealAsync(dir, "parent bytes");
        new PhysicalImageFileSystem().SetReadOnly(sealedParent.Path, isReadOnly: false);

        var result = await verifier.VerifyAsync(sealedParent, _requiredGuestAgent, TestContext.Current.CancellationToken);

        Assert.Equal(GoldenImageStatus.Writable, result.Status);
        Assert.Contains("read-only", result.Message);
    }

    [Fact]
    public async Task Guest_agent_version_mismatch_is_reported()
    {
        using var dir = new TempImageDirectory();
        var (verifier, sealedParent) = await SealAsync(dir, "parent bytes");

        var result = await verifier.VerifyAsync(sealedParent, "2.0.0", TestContext.Current.CancellationToken);

        Assert.Equal(GoldenImageStatus.GuestAgentMismatch, result.Status);
        Assert.Contains("2.0.0", result.Message);
    }

    [Fact]
    public async Task Hash_mismatch_is_reported_before_writable()
    {
        using var dir = new TempImageDirectory();
        var (verifier, sealedParent) = await SealAsync(dir, "original bytes");
        WriteTamperedBytes(sealedParent.Path, "tampered bytes");
        new PhysicalImageFileSystem().SetReadOnly(sealedParent.Path, isReadOnly: false);

        var result = await verifier.VerifyAsync(sealedParent, _requiredGuestAgent, TestContext.Current.CancellationToken);

        Assert.Equal(GoldenImageStatus.HashMismatch, result.Status);
    }

    [Fact]
    public async Task Writable_is_reported_before_guest_agent_mismatch()
    {
        using var dir = new TempImageDirectory();
        var (verifier, sealedParent) = await SealAsync(dir, "parent bytes");
        new PhysicalImageFileSystem().SetReadOnly(sealedParent.Path, isReadOnly: false);

        var result = await verifier.VerifyAsync(sealedParent, "2.0.0", TestContext.Current.CancellationToken);

        Assert.Equal(GoldenImageStatus.Writable, result.Status);
    }

    [Fact]
    public async Task Recorded_hash_comparison_ignores_hex_case()
    {
        using var dir = new TempImageDirectory();
        var (verifier, sealedParent) = await SealAsync(dir, "parent bytes");
        var upper = sealedParent with { Sha256 = sealedParent.Sha256.ToUpperInvariant() };

        var result = await verifier.VerifyAsync(upper, _requiredGuestAgent, TestContext.Current.CancellationToken);

        Assert.Equal(GoldenImageStatus.Ok, result.Status);
    }

    [Fact]
    public async Task Verification_never_writes_the_image()
    {
        const string path = "parent.img";
        var fs = new FakeImageFileSystem();
        fs.Add(path, "parent"u8.ToArray(), isReadOnly: true);
        var record = new SealedImage(GoldenImageOs.Linux, 3, ImageTestData.Sha256Of("parent"), _requiredGuestAgent, path);
        var verifier = new GoldenImageVerifier(fs, new ImageHasher(fs));

        var result = await verifier.VerifyAsync(record, _requiredGuestAgent, TestContext.Current.CancellationToken);

        Assert.True(result.IsOk);
        Assert.Equal(0, fs.MutationCount);
    }

    [Fact]
    public async Task Verification_detects_a_writable_fake_without_changing_it()
    {
        const string path = "parent.img";
        var fs = new FakeImageFileSystem();
        fs.Add(path, "parent"u8.ToArray(), isReadOnly: false);
        var record = new SealedImage(GoldenImageOs.Linux, 3, ImageTestData.Sha256Of("parent"), _requiredGuestAgent, path);
        var verifier = new GoldenImageVerifier(fs, new ImageHasher(fs));

        var result = await verifier.VerifyAsync(record, _requiredGuestAgent, TestContext.Current.CancellationToken);

        Assert.Equal(GoldenImageStatus.Writable, result.Status);
        Assert.Equal(0, fs.MutationCount);
    }

    [Fact]
    public async Task Null_record_is_refused()
    {
        using var dir = new TempImageDirectory();
        var fs = new PhysicalImageFileSystem();
        var verifier = new GoldenImageVerifier(fs, new ImageHasher(fs));

        await Assert.ThrowsAsync<ArgumentNullException>(() => verifier.VerifyAsync(null!, _requiredGuestAgent, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Empty_expected_guest_agent_is_refused()
    {
        using var dir = new TempImageDirectory();
        var (verifier, sealedParent) = await SealAsync(dir, "parent bytes");

        await Assert.ThrowsAnyAsync<ArgumentException>(() => verifier.VerifyAsync(sealedParent, " ", TestContext.Current.CancellationToken));
    }

    /// <summary>Seals a parent with the real file system and returns a verifier for the same file system.</summary>
    private static async Task<(GoldenImageVerifier Verifier, SealedImage Sealed)> SealAsync(TempImageDirectory dir, string content)
    {
        var path = dir.WriteFile("parent.img", content);
        var fs = new PhysicalImageFileSystem();
        var hasher = new ImageHasher(fs);
        var sealedParent = await new GoldenImageSealer(fs, hasher).SealAsync(ImageTestData.LinuxRecipe(), path, TestContext.Current.CancellationToken);
        return (new GoldenImageVerifier(fs, hasher), sealedParent);
    }

    /// <summary>Overwrites a sealed file. The helper clears read-only first, as a tampering writer would have to.</summary>
    private static void WriteTamperedBytes(string path, string content)
    {
        new PhysicalImageFileSystem().SetReadOnly(path, isReadOnly: false);
        File.WriteAllText(path, content);
    }
}
