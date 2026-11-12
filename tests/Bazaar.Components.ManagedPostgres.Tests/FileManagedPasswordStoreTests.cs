using Bazaar.Components.ManagedPostgres.Secrets;

namespace Bazaar.Components.ManagedPostgres.Tests;

public sealed class FileManagedPasswordStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bazaar-pw-store", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task A_missing_store_loads_as_null()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The file store is for Linux and macOS.");
        }

        var store = new FileManagedPasswordStore(Path.Combine(_root, "secrets", "pw"));

        Assert.Null(await store.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_saved_password_round_trips_and_the_file_is_owner_only()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The file store is for Linux and macOS.");
        }

        var path = Path.Combine(_root, "secrets", "pw");
        var store = new FileManagedPasswordStore(path);

        await store.SaveAsync("p4ss-round-trip", TestContext.Current.CancellationToken);

        Assert.Equal("p4ss-round-trip", await store.LoadAsync(TestContext.Current.CancellationToken));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }

        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task An_empty_password_is_refused()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The file store is for Linux and macOS.");
        }

        var store = new FileManagedPasswordStore(Path.Combine(_root, "pw"));

        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(string.Empty, TestContext.Current.CancellationToken));
    }
}
