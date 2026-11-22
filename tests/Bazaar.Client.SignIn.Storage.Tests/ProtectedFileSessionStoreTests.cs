using System.Text;
using Bazaar.Client.SignIn;

namespace Bazaar.Client.SignIn.Storage.Tests;

public sealed class ProtectedFileSessionStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bazaar-session-" + Guid.NewGuid().ToString("N"));
    private readonly ClientSession _session = new("access-1", "refresh-1", new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero));

    private string FilePath => Path.Combine(_directory, "nested", "session.bin");

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task Load_returns_null_when_nothing_was_saved()
    {
        using var store = new ProtectedFileSessionStore(FilePath, new XorProtector());
        Assert.Null(await store.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Saved_session_survives_a_new_store_over_the_same_file()
    {
        using (var first = new ProtectedFileSessionStore(FilePath, new XorProtector()))
        {
            await first.SaveAsync(_session, TestContext.Current.CancellationToken);
        }

        using var second = new ProtectedFileSessionStore(FilePath, new XorProtector());
        Assert.Equal(_session, await second.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task File_on_disk_never_holds_a_token_in_the_clear()
    {
        using var store = new ProtectedFileSessionStore(FilePath, new XorProtector());
        await store.SaveAsync(_session, TestContext.Current.CancellationToken);

        var text = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(FilePath, TestContext.Current.CancellationToken));
        Assert.DoesNotContain("access-1", text, StringComparison.Ordinal);
        Assert.DoesNotContain("refresh-1", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Save_replaces_the_earlier_session_and_leaves_no_temporary_file()
    {
        using var store = new ProtectedFileSessionStore(FilePath, new XorProtector());
        await store.SaveAsync(_session, TestContext.Current.CancellationToken);
        var next = _session with { AccessToken = "access-2", RefreshToken = "refresh-2" };
        await store.SaveAsync(next, TestContext.Current.CancellationToken);

        Assert.Equal(next, await store.LoadAsync(TestContext.Current.CancellationToken));
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Fact]
    public async Task Clear_forgets_the_session_and_is_safe_to_repeat()
    {
        using var store = new ProtectedFileSessionStore(FilePath, new XorProtector());
        await store.SaveAsync(_session, TestContext.Current.CancellationToken);
        await store.ClearAsync(TestContext.Current.CancellationToken);
        await store.ClearAsync(TestContext.Current.CancellationToken);

        Assert.Null(await store.LoadAsync(TestContext.Current.CancellationToken));
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public async Task File_the_protector_cannot_open_reads_as_signed_out_and_is_removed()
    {
        using (var writer = new ProtectedFileSessionStore(FilePath, new XorProtector()))
        {
            await writer.SaveAsync(_session, TestContext.Current.CancellationToken);
        }

        using var reader = new ProtectedFileSessionStore(FilePath, new RefusingProtector());
        Assert.Null(await reader.LoadAsync(TestContext.Current.CancellationToken));
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public async Task Damaged_file_reads_as_signed_out()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        await File.WriteAllBytesAsync(FilePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        using var store = new ProtectedFileSessionStore(FilePath, new XorProtector());
        Assert.Null(await store.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Sealed_file_with_blank_tokens_reads_as_signed_out()
    {
        var protector = new XorProtector();
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var plain = Encoding.UTF8.GetBytes("""{"access":"","refresh":"","expires":"2026-11-22T12:00:00+00:00"}""");
        await File.WriteAllBytesAsync(FilePath, protector.Protect(plain), TestContext.Current.CancellationToken);

        using var store = new ProtectedFileSessionStore(FilePath, protector);
        Assert.Null(await store.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Concurrent_saves_leave_one_whole_session()
    {
        using var store = new ProtectedFileSessionStore(FilePath, new XorProtector());
        var sessions = Enumerable.Range(0, 20).Select(i => _session with { AccessToken = "a" + i, RefreshToken = "r" + i }).ToArray();

        await Task.WhenAll(sessions.Select(s => store.SaveAsync(s, TestContext.Current.CancellationToken)));

        var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(loaded);
        Assert.Equal(loaded.AccessToken[1..], loaded.RefreshToken[1..]);
    }

    [Fact]
    public void Constructor_rejects_a_blank_path_and_a_missing_protector()
    {
        Assert.Throws<ArgumentException>(() => new ProtectedFileSessionStore(" ", new XorProtector()));
        Assert.Throws<ArgumentNullException>(() => new ProtectedFileSessionStore(FilePath, null!));
    }

    /// <summary>A reversible stand-in: enough to prove the store seals and unseals through the port.</summary>
    private sealed class XorProtector : ISecretProtector
    {
        private const byte Key = 0x5A;

        public byte[] Protect(ReadOnlySpan<byte> plain)
        {
            var result = plain.ToArray();
            for (var i = 0; i < result.Length; i++) result[i] ^= Key;
            return result;
        }

        public byte[]? Unprotect(ReadOnlySpan<byte> sealedBytes) => Protect(sealedBytes);
    }

    private sealed class RefusingProtector : ISecretProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> plain) => plain.ToArray();

        public byte[]? Unprotect(ReadOnlySpan<byte> sealedBytes) => null;
    }
}
