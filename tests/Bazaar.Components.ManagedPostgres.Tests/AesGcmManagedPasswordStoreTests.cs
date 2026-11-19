using System.Security.Cryptography;
using System.Text;
using Bazaar.Components.ManagedPostgres.Secrets;

namespace Bazaar.Components.ManagedPostgres.Tests;

public sealed class AesGcmManagedPasswordStoreTests : IDisposable
{
    private readonly string _directory = ProcessTestSupport.NewTempDirectory();

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task Load_returns_null_when_nothing_is_saved()
    {
        var store = new AesGcmManagedPasswordStore(Path.Combine(_directory, "superuser.enc"), new FixedKey(Key(1)));

        Assert.Null(await store.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Save_then_load_round_trips_the_password()
    {
        var store = new AesGcmManagedPasswordStore(Path.Combine(_directory, "superuser.enc"), new FixedKey(Key(1)));

        await store.SaveAsync("s3cret-pass", TestContext.Current.CancellationToken);

        Assert.Equal("s3cret-pass", await store.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Concurrent_saves_leave_one_complete_password_and_no_temporary_file()
    {
        var path = Path.Combine(_directory, "superuser.enc");
        var store = new AesGcmManagedPasswordStore(path, new FixedKey(Key(1)));
        var passwords = Enumerable.Range(0, 24).Select(i => "pw-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + new string('x', 4096)).ToArray();

        var saves = passwords.Select(password => Task.Run(() => store.SaveAsync(password, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken));
        await Task.WhenAll(saves);

        Assert.Contains(await store.LoadAsync(TestContext.Current.CancellationToken), passwords);
        Assert.Equal([path], Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task The_file_never_contains_the_plaintext_password()
    {
        var path = Path.Combine(_directory, "superuser.enc");
        const string password = "PlaintextMustNotAppear-42";
        var store = new AesGcmManagedPasswordStore(path, new FixedKey(Key(1)));

        await store.SaveAsync(password, TestContext.Current.CancellationToken);

        var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(password, Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.True(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(password)) < 0);
    }

    [Fact]
    public async Task A_different_key_cannot_read_the_store()
    {
        var path = Path.Combine(_directory, "superuser.enc");
        await new AesGcmManagedPasswordStore(path, new FixedKey(Key(1))).SaveAsync("pw", TestContext.Current.CancellationToken);
        var other = new AesGcmManagedPasswordStore(path, new FixedKey(Key(2)));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => other.LoadAsync(TestContext.Current.CancellationToken));

        Assert.DoesNotContain("pw", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tampered_store_is_refused()
    {
        var path = Path.Combine(_directory, "superuser.enc");
        var store = new AesGcmManagedPasswordStore(path, new FixedKey(Key(1)));
        await store.SaveAsync("pw", TestContext.Current.CancellationToken);
        var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        bytes[^1] ^= 0x01;
        await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_key_that_is_not_32_bytes_is_refused_before_any_write()
    {
        var path = Path.Combine(_directory, "superuser.enc");
        var store = new AesGcmManagedPasswordStore(path, new FixedKey(new byte[16]));

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync("pw", TestContext.Current.CancellationToken));

        Assert.False(File.Exists(path));
    }

    private static byte[] Key(byte fill)
    {
        var key = new byte[32];
        Array.Fill(key, fill);
        return key;
    }

    private sealed class FixedKey(byte[] key) : IManagedStoreKeyProvider
    {
        public Task<byte[]> GetKeyAsync(CancellationToken cancellationToken) => Task.FromResult(key);
    }
}
