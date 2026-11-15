using Bazaar.Server.Identity.Keys;

namespace Bazaar.Server.Tests.Identity;

/// <summary>The default file-backed KEK (docs/design/04 Question 2). Database-free.</summary>
public sealed class FileKekProviderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bazaar-kek-" + Guid.CreateVersion7().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private string KeyPath => Path.Combine(_directory, "keys", "kek.bin");

    [Fact]
    public async Task A_wrap_unwraps_under_a_reopened_provider_with_the_same_key()
    {
        var ct = TestContext.Current.CancellationToken;
        var aad = Guid.CreateVersion7().ToByteArray();
        var dek = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);

        using var first = FileKekProvider.Open(KeyPath);
        var wrapped = await first.WrapAsync(dek, aad, ct);
        using var reopened = FileKekProvider.Open(KeyPath);

        Assert.Equal(dek, await reopened.UnwrapAsync(wrapped, aad, ct));
    }

    [Fact]
    public async Task A_wrap_does_not_unwrap_under_different_associated_data()
    {
        var ct = TestContext.Current.CancellationToken;
        using var kek = FileKekProvider.Open(KeyPath);
        var wrapped = await kek.WrapAsync(new byte[32], Guid.CreateVersion7().ToByteArray(), ct);

        await Assert.ThrowsAsync<KekUnwrapException>(() => kek.UnwrapAsync(wrapped, Guid.CreateVersion7().ToByteArray(), ct).AsTask());
    }

    [Fact]
    public void The_first_open_creates_a_32_byte_key_file()
    {
        using var kek = FileKekProvider.Open(KeyPath);

        Assert.True(File.Exists(KeyPath));
        Assert.Equal(32, new FileInfo(KeyPath).Length);
        Assert.Equal(FileKekProvider.Id, kek.KekId);
    }

    [Fact]
    public void A_key_file_of_the_wrong_length_is_refused()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(KeyPath)!);
        File.WriteAllBytes(KeyPath, new byte[16]);

        Assert.Throws<InvalidDataException>(() => FileKekProvider.Open(KeyPath));
    }

    [Fact]
    public void A_relative_key_path_is_refused()
    {
        Assert.Throws<ArgumentException>(() => FileKekProvider.Open(Path.Combine("relative", "kek.bin")));
    }

    [Fact]
    public void On_posix_hosts_the_key_file_is_readable_and_writable_by_its_owner_only()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var kek = FileKekProvider.Open(KeyPath);

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(KeyPath));
    }
}
