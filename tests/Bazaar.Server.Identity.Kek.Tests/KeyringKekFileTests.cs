using System.Security.Cryptography;
using Bazaar.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Identity.Kek.Tests;

/// <summary>Doc 61 Q2: the <c>file</c> provider's key file sealed by an OS keyring, the audited fallback and the fail-closed cases.</summary>
public sealed class KeyringKekFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bazaar-kek-keyring-" + Guid.CreateVersion7().ToString("N"));
    private readonly RecordingAudit _audit = new();

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string KeyPath => Path.Combine(_root, "keys", "kek.bin");

    private string MarkerPath => Path.Combine(_root, "keys", "kek.sealed");

    private string PremigratePath => KeyPath + ".premigrate";

    private Task<FileKekProvider> Open(IKekKeyring? keyring, KekKeyringMode mode = KekKeyringMode.Auto, IKekWrapVerifier? verifier = null, TimeSpan? lockTimeout = null) =>
        FileKekProvider.OpenAsync(KeyPath, keyring, mode, _audit, wrapVerifier: verifier, lockTimeout: lockTimeout, cancellationToken: TestContext.Current.CancellationToken).AsTask();

    [Fact]
    public async Task A_new_key_file_is_sealed_by_the_keyring_and_reopens()
    {
        var ct = TestContext.Current.CancellationToken;
        var keyring = new FakeKeyring();
        var dek = RandomNumberGenerator.GetBytes(32);
        var aad = "row-1"u8.ToArray();

        using var first = await Open(keyring);
        var wrapped = await first.WrapAsync(dek, aad, ct);
        using var second = await Open(keyring);

        Assert.Equal(dek, await second.UnwrapAsync(wrapped, aad, ct));
        Assert.Equal(FileKekProvider.Id, second.KekId);
        var stored = await File.ReadAllBytesAsync(KeyPath, ct);
        Assert.Equal("BZKF"u8.ToArray(), stored[..4]);
        Assert.NotEqual(32, stored.Length);
        Assert.Equal(["kek.keyfile.sealed"], _audit.Kinds);
        Assert.Single(keyring.Entries);
    }

    [Fact]
    public async Task The_sealed_file_alone_is_not_the_key()
    {
        var ct = TestContext.Current.CancellationToken;
        using var sealedKek = await Open(new FakeKeyring());
        var wrapped = await sealedKek.WrapAsync(new byte[32], "a"u8.ToArray(), ct);

        // Someone who copies the file and treats any 32 bytes of it as the key cannot unwrap.
        var stored = await File.ReadAllBytesAsync(KeyPath, ct);
        for (var offset = 0; offset + 32 <= stored.Length; offset++)
        {
            var path = Path.Combine(_root, "guess", offset + ".bin");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, stored.AsSpan(offset, 32).ToArray(), ct);
            using var guess = FileKekProvider.Open(path);
            await Assert.ThrowsAsync<KekUnwrapException>(() => guess.UnwrapAsync(wrapped, "a"u8.ToArray(), ct).AsTask());
        }
    }

    [Fact]
    public async Task A_plain_key_file_is_migrated_and_earlier_wraps_still_open()
    {
        var ct = TestContext.Current.CancellationToken;
        byte[] wrapped;
        using (var plain = FileKekProvider.Open(KeyPath))
        {
            wrapped = await plain.WrapAsync(new byte[] { 7, 7, 7 }, "x"u8.ToArray(), ct);
        }

        var keyring = new FakeKeyring();
        using var migrated = await Open(keyring);

        Assert.Equal(new byte[] { 7, 7, 7 }, await migrated.UnwrapAsync(wrapped, "x"u8.ToArray(), ct));
        Assert.Equal(["kek.keyfile.migrated", "kek.keyfile.premigrate_kept"], _audit.Kinds);
        Assert.NotEqual(32, new FileInfo(KeyPath).Length);
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(KeyPath)!, "*.tmp"));

        using var reopened = await Open(keyring);
        Assert.Equal(new byte[] { 7, 7, 7 }, await reopened.UnwrapAsync(wrapped, "x"u8.ToArray(), ct));
    }

    [Fact]
    public async Task Without_a_keyring_the_plain_file_is_used_with_an_audited_warning()
    {
        var ct = TestContext.Current.CancellationToken;
        var keyring = new FakeKeyring { Available = false };

        using var kek = await Open(keyring);
        var wrapped = await kek.WrapAsync(new byte[] { 1 }, "y"u8.ToArray(), ct);
        using var noKeyringProvider = await Open(null);

        Assert.Equal(32, new FileInfo(KeyPath).Length);
        Assert.Equal(new byte[] { 1 }, await noKeyringProvider.UnwrapAsync(wrapped, "y"u8.ToArray(), ct));
        Assert.Equal(["kek.keyring.fallback", "kek.keyring.fallback"], _audit.Kinds);
        Assert.All(_audit.Events, e => Assert.DoesNotContain("BZKF", e.Detail, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Required_mode_refuses_to_start_without_a_keyring()
    {
        await Assert.ThrowsAsync<KekKeyringException>(() => Open(new FakeKeyring { Available = false }, KekKeyringMode.Required));

        Assert.False(File.Exists(KeyPath));
        Assert.Equal(["kek.keyring.required_unavailable"], _audit.Kinds);
    }

    [Fact]
    public async Task A_failed_audit_record_fails_the_fallback()
    {
        _audit.Fail = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => Open(new FakeKeyring { Available = false }));
        Assert.False(File.Exists(KeyPath));
    }

    [Fact]
    public async Task A_sealed_file_never_falls_back_when_the_keyring_is_gone()
    {
        var keyring = new FakeKeyring();
        (await Open(keyring)).Dispose();
        var before = await File.ReadAllBytesAsync(KeyPath, TestContext.Current.CancellationToken);

        keyring.Available = false;
        await Assert.ThrowsAsync<KekKeyringException>(() => Open(keyring));
        await Assert.ThrowsAsync<KekKeyringException>(() => Open(null));

        Assert.Equal(before, await File.ReadAllBytesAsync(KeyPath, TestContext.Current.CancellationToken));
        Assert.Contains("kek.keyring.unavailable_sealed", _audit.Kinds);
    }

    [Fact]
    public async Task A_lost_keyring_entry_or_another_keyring_fails_closed()
    {
        var keyring = new FakeKeyring();
        (await Open(keyring)).Dispose();

        await Assert.ThrowsAsync<KekKeyringException>(() => Open(new FakeKeyring()));
        await Assert.ThrowsAsync<KekKeyringException>(() => Open(new FakeKeyring { KindName = "other" }));
    }

    [Fact]
    public async Task A_tampered_sealed_file_fails_closed()
    {
        var keyring = new FakeKeyring();
        (await Open(keyring)).Dispose();
        var bytes = await File.ReadAllBytesAsync(KeyPath, TestContext.Current.CancellationToken);
        bytes[^3] ^= 0x01;
        await File.WriteAllBytesAsync(KeyPath, bytes, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<KekKeyringException>(() => Open(keyring));
    }

    [Fact]
    public async Task A_plain_file_swapped_in_over_a_sealed_one_is_refused_as_a_downgrade()
    {
        var keyring = new FakeKeyring();
        (await Open(keyring)).Dispose();
        await File.WriteAllBytesAsync(KeyPath, RandomNumberGenerator.GetBytes(32), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<KekKeyringException>(() => Open(keyring));
        Assert.Contains("kek.keyfile.downgrade_refused", _audit.Kinds);
        Assert.Equal(32, new FileInfo(KeyPath).Length);
    }

    [Fact]
    public async Task A_keyring_that_returns_a_different_key_leaves_the_plain_file_untouched()
    {
        var ct = TestContext.Current.CancellationToken;
        FileKekProvider.Open(KeyPath).Dispose();
        var before = await File.ReadAllBytesAsync(KeyPath, ct);

        await Assert.ThrowsAsync<KekKeyringException>(() => Open(new FakeKeyring { CorruptOpen = true }));

        Assert.Equal(before, await File.ReadAllBytesAsync(KeyPath, ct));
    }

    [Fact]
    public async Task Protector_keyring_round_trips_and_binds_the_entry()
    {
        var ct = TestContext.Current.CancellationToken;
        var keyring = new KeyProtectorKekKeyring(TestProtector.Create(), "dpapi");
        var key = RandomNumberGenerator.GetBytes(32);

        var sealedKey = await keyring.SealAsync("e", key, "aad-1"u8.ToArray(), ct);

        Assert.Equal(key, await keyring.OpenAsync("e", sealedKey, "aad-1"u8.ToArray(), ct));
        await Assert.ThrowsAsync<KekKeyringException>(() => keyring.OpenAsync("e", sealedKey, "aad-2"u8.ToArray(), ct).AsTask());
    }

    [Fact]
    public void Keyring_mode_parses_auto_and_required_only()
    {
        Assert.Equal(KekKeyringMode.Auto, KekProviderFactory.ParseKeyringMode(null));
        Assert.Equal(KekKeyringMode.Auto, KekProviderFactory.ParseKeyringMode("auto"));
        Assert.Equal(KekKeyringMode.Required, KekProviderFactory.ParseKeyringMode("Required"));
        Assert.Throws<ArgumentException>(() => KekProviderFactory.ParseKeyringMode("off"));
    }

    [Fact]
    public void Component_seals_the_file_kind_with_a_registered_keyring()
    {
        var configuration = new FakeConfiguration(new Dictionary<string, string?>
        {
            ["Bazaar:Host:DataRoot"] = _root,
            [KekComponent.KekProviderKey] = "file",
            [KekComponent.KekKeyringKey] = "required",
        });
        var services = new ServiceCollection();
        services.AddSingleton<IKekKeyring>(new FakeKeyring());
        services.AddSingleton<IKekCustodyAuditSink>(_audit);

        services.AddBazaarComponents(configuration, [new KekComponent()]);
        using var provider = services.BuildServiceProvider();

        Assert.IsType<FileKekProvider>(provider.GetRequiredService<IKekProvider>());
        Assert.Equal(["kek.keyfile.sealed"], _audit.Kinds);
    }

    [Fact]
    public async Task A_32_byte_file_that_starts_like_a_sealed_file_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(Path.GetDirectoryName(KeyPath)!);
        var truncated = RandomNumberGenerator.GetBytes(32);
        "BZKF"u8.CopyTo(truncated);
        truncated[4] = 1;
        await File.WriteAllBytesAsync(KeyPath, truncated, ct);

        await Assert.ThrowsAsync<KekKeyringException>(() => Open(null));
        await Assert.ThrowsAsync<KekKeyringException>(() => Open(new FakeKeyring()));

        Assert.Equal(truncated, await File.ReadAllBytesAsync(KeyPath, ct));
        Assert.Contains("kek.keyfile.truncated_sealed", _audit.Kinds);
    }

    [Fact]
    public async Task A_migration_is_refused_when_the_plain_key_unwraps_no_existing_data_key()
    {
        var ct = TestContext.Current.CancellationToken;
        FileKekProvider.Open(KeyPath).Dispose();
        var before = await File.ReadAllBytesAsync(KeyPath, ct);
        var keyring = new FakeKeyring();

        await Assert.ThrowsAsync<KekKeyringException>(() => Open(keyring, verifier: new StubVerifier(KekWrapCheck.Failed)));

        Assert.Equal(before, await File.ReadAllBytesAsync(KeyPath, ct));
        Assert.Empty(keyring.Entries);
        Assert.False(File.Exists(PremigratePath));
        Assert.False(File.Exists(MarkerPath));
        Assert.Contains("kek.keyfile.migration_refused", _audit.Kinds);
    }

    [Fact]
    public async Task The_pre_migration_copy_is_kept_until_the_sealed_key_is_verified()
    {
        var ct = TestContext.Current.CancellationToken;
        FileKekProvider.Open(KeyPath).Dispose();
        var plain = await File.ReadAllBytesAsync(KeyPath, ct);

        // No verifier: nothing can confirm the sealed key, so the copy stays (owner-only) and the open says so.
        (await Open(new FakeKeyring())).Dispose();
        Assert.Equal(plain, await File.ReadAllBytesAsync(PremigratePath, ct));
        Assert.Contains("kek.keyfile.premigrate_kept", _audit.Kinds);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(PremigratePath));

        // The sealed key fails the check: the copy stays and the start fails.
        var keyring = new FakeKeyring();
        File.Delete(KeyPath);
        File.Delete(MarkerPath);
        await File.WriteAllBytesAsync(KeyPath, plain, ct);
        await Assert.ThrowsAsync<KekKeyringException>(() => Open(keyring, verifier: new StubVerifier(KekWrapCheck.Verified, KekWrapCheck.Failed)));
        Assert.True(File.Exists(PremigratePath));

        // Once a verifier confirms the sealed key, the copy is wiped.
        (await Open(keyring, verifier: new StubVerifier(KekWrapCheck.Verified))).Dispose();
        Assert.False(File.Exists(PremigratePath));
        Assert.Contains("kek.keyfile.premigrate_removed", _audit.Kinds);
    }

    [Fact]
    public async Task Sealing_is_sticky_a_plain_file_is_refused_whatever_the_keyring_says()
    {
        var ct = TestContext.Current.CancellationToken;
        var keyring = new FakeKeyring();
        (await Open(keyring)).Dispose();
        Assert.True(File.Exists(MarkerPath));
        var plain = RandomNumberGenerator.GetBytes(32);
        await File.WriteAllBytesAsync(KeyPath, plain, ct);

        // No keyring at all (the Windows-style case), an offline one and a working one: all refuse.
        await Assert.ThrowsAsync<KekKeyringException>(() => Open(null));
        await Assert.ThrowsAsync<KekKeyringException>(() => Open(new FakeKeyring { Available = false }));
        await Assert.ThrowsAsync<KekKeyringException>(() => Open(keyring));
        Assert.Equal(plain, await File.ReadAllBytesAsync(KeyPath, ct));

        // A deleted sealed file is not replaced by a fresh key either.
        File.Delete(KeyPath);
        await Assert.ThrowsAsync<KekKeyringException>(() => Open(keyring));
        Assert.False(File.Exists(KeyPath));
        Assert.Contains("kek.keyfile.missing_sealed", _audit.Kinds);
    }

    [Fact]
    public async Task A_sealed_file_without_the_marker_gets_it_on_the_next_clean_open()
    {
        var keyring = new FakeKeyring();
        (await Open(keyring)).Dispose();
        File.Delete(MarkerPath);

        (await Open(keyring)).Dispose();

        Assert.True(File.Exists(MarkerPath));
    }

    [Fact]
    public async Task An_interrupted_migration_of_the_same_key_is_finished_but_a_swapped_file_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        FileKekProvider.Open(KeyPath).Dispose();
        var plain = await File.ReadAllBytesAsync(KeyPath, ct);
        var keyring = new FakeKeyring();
        // The crash: the keyring entry and its commitment exist, the file was not yet replaced.
        await keyring.SealAsync(KeyringKekKeyFile.EntryName(KeyPath), plain, new byte[1], ct);

        using var finished = await Open(keyring);

        Assert.Equal("BZKF"u8.ToArray(), (await File.ReadAllBytesAsync(KeyPath, ct))[..4]);
        Assert.Contains("kek.keyfile.migrated", _audit.Kinds);

        // Another install's plain key next to an entry whose commitment is for a different key is a downgrade.
        var other = new FakeKeyring();
        await other.SealAsync(KeyringKekKeyFile.EntryName(KeyPath), RandomNumberGenerator.GetBytes(32), new byte[1], ct);
        File.Delete(KeyPath);
        File.Delete(MarkerPath);
        await File.WriteAllBytesAsync(KeyPath, plain, ct);
        await Assert.ThrowsAsync<KekKeyringException>(() => Open(other));
        Assert.Equal(plain, await File.ReadAllBytesAsync(KeyPath, ct));

        // An entry with no commitment at all (an older or foreign one) is refused as well.
        other.Commitments.Clear();
        await Assert.ThrowsAsync<KekKeyringException>(() => Open(other));
    }

    [Fact]
    public async Task The_entry_name_comes_from_the_sealed_header_not_from_the_path()
    {
        var ct = TestContext.Current.CancellationToken;
        var keyring = new FakeKeyring();
        var dek = RandomNumberGenerator.GetBytes(32);
        byte[] wrapped;
        using (var first = await Open(keyring))
        {
            wrapped = await first.WrapAsync(dek, "a"u8.ToArray(), ct);
        }

        var moved = Path.Combine(_root, "moved", "keys", "kek.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        File.Copy(KeyPath, moved);
        File.Copy(MarkerPath, Path.Combine(Path.GetDirectoryName(moved)!, "kek.sealed"));

        using var elsewhere = await FileKekProvider.OpenAsync(moved, keyring, KekKeyringMode.Auto, _audit, cancellationToken: ct);

        Assert.Equal(dek, await elsewhere.UnwrapAsync(wrapped, "a"u8.ToArray(), ct));
        Assert.Single(keyring.Entries);

        // Changing the entry name in the header does not reach another entry's key.
        var bytes = await File.ReadAllBytesAsync(moved, ct);
        bytes[6 + "fake".Length + 1] ^= 0x01;
        await File.WriteAllBytesAsync(moved, bytes, ct);
        await Assert.ThrowsAsync<KekKeyringException>(() => FileKekProvider.OpenAsync(moved, keyring, KekKeyringMode.Auto, _audit, cancellationToken: ct).AsTask());
    }

    [Fact]
    public async Task New_key_files_and_their_directory_are_owner_only_from_creation()
    {
        if (OperatingSystem.IsWindows())
            return; // POSIX modes only.
        (await Open(null)).Dispose();
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(KeyPath));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.GetDirectoryName(KeyPath)!));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(KeyPath + ".lock"));

        var sealedRoot = new KeyringKekKeyFileProbe(_root + "-sealed");
        await sealedRoot.OpenSealedAsync(_audit, TestContext.Current.CancellationToken);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(sealedRoot.KeyPath));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(sealedRoot.MarkerPath));
        sealedRoot.Dispose();
    }

    [Fact]
    public async Task A_lock_that_cannot_be_taken_in_time_is_audited_and_refused()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(KeyPath)!);
        await using var held = new FileStream(KeyPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        await Assert.ThrowsAsync<KekKeyringException>(() => Open(new FakeKeyring(), lockTimeout: TimeSpan.FromMilliseconds(200)));

        Assert.Equal(["kek.keyfile.lock_timeout"], _audit.Kinds);
        Assert.False(File.Exists(KeyPath));
    }

    [Fact]
    public void Component_does_not_need_a_custody_sink_or_a_keyring_for_other_kinds()
    {
        var configuration = new FakeConfiguration(new Dictionary<string, string?>
        {
            ["Bazaar:Host:DataRoot"] = _root,
            [KekComponent.KekProviderKey] = "openbao",
        });
        var services = new ServiceCollection();
        services.AddBazaarComponents(configuration, [new KekComponent()]);
        using var provider = services.BuildServiceProvider();

        // openbao is not built, and that is what is reported: not a missing IKekCustodyAuditSink.
        Assert.Throws<NotSupportedException>(() => provider.GetRequiredService<IKekProvider>());

        var file = new FakeConfiguration(new Dictionary<string, string?> { ["Bazaar:Host:DataRoot"] = _root, [KekComponent.KekProviderKey] = "file" });
        var withoutSink = new ServiceCollection();
        withoutSink.AddBazaarComponents(file, [new KekComponent()]);
        using var fileProvider = withoutSink.BuildServiceProvider();
        Assert.Throws<InvalidOperationException>(() => fileProvider.GetRequiredService<IKekProvider>());
    }

    [Fact]
    public void Component_bounds_the_keyring_wait_and_cancels_it()
    {
        var configuration = new FakeConfiguration(new Dictionary<string, string?>
        {
            ["Bazaar:Host:DataRoot"] = _root,
            [KekComponent.KekProviderKey] = "file",
            [KekComponent.OpenTimeoutSecondsKey] = "1",
        });
        var hanging = new FakeKeyring { Hang = true };
        var services = new ServiceCollection();
        services.AddSingleton<IKekKeyring>(hanging);
        services.AddSingleton<IKekCustodyAuditSink>(_audit);
        services.AddBazaarComponents(configuration, [new KekComponent()]);
        using var provider = services.BuildServiceProvider();
        var started = DateTime.UtcNow;

        var ex = Assert.Throws<KekKeyringException>(() => provider.GetRequiredService<IKekProvider>());

        Assert.Contains("did not finish", ex.Message, StringComparison.Ordinal);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(30));
    }

    private sealed class StubVerifier(params KekWrapCheck[] results) : IKekWrapVerifier
    {
        private int _calls;

        public ValueTask<KekWrapCheck> CheckAsync(IKekProvider candidate, CancellationToken cancellationToken) =>
            ValueTask.FromResult(results[Math.Min(_calls++, results.Length - 1)]);
    }

    /// <summary>A second, separate install root sealed by a fake keyring, to read back modes without disturbing the main root.</summary>
    private sealed class KeyringKekKeyFileProbe(string root) : IDisposable
    {
        public string KeyPath { get; } = Path.Combine(root, "keys", "kek.bin");

        public string MarkerPath { get; } = Path.Combine(root, "keys", "kek.sealed");

        public async Task OpenSealedAsync(IKekCustodyAuditSink audit, CancellationToken ct) =>
            (await FileKekProvider.OpenAsync(KeyPath, new FakeKeyring(), KekKeyringMode.Auto, audit, cancellationToken: ct)).Dispose();

        public void Dispose() => Directory.Delete(root, recursive: true);
    }

    private sealed class RecordingAudit : IKekCustodyAuditSink
    {
        public List<KekCustodyAuditEvent> Events { get; } = [];

        public bool Fail { get; set; }

        public List<string> Kinds => [.. Events.Select(e => e.Kind)];

        public ValueTask RecordAsync(KekCustodyAuditEvent auditEvent, CancellationToken cancellationToken)
        {
            if (Fail)
                throw new InvalidOperationException("audit down");
            Events.Add(auditEvent);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>An in-memory keyring with named entries, like the Secret Service.</summary>
    private sealed class FakeKeyring : IKekKeyring
    {
        public Dictionary<string, byte[]> Entries { get; } = [];

        public Dictionary<string, byte[]> Commitments { get; } = [];

        public bool Hang { get; init; }

        public bool Available { get; set; } = true;

        public bool CorruptOpen { get; init; }

        public string KindName { get; init; } = "fake";

        public string Kind => KindName;

        public async ValueTask<KekKeyringProbe> ProbeAsync(string entryName, CancellationToken cancellationToken)
        {
            if (Hang)
                await Task.Delay(Timeout.Infinite, cancellationToken);
            return Available ? new KekKeyringProbe(true, Entries.ContainsKey(entryName), null) : KekKeyringProbe.Unavailable("test keyring offline");
        }

        public ValueTask<bool> VerifyCommitmentAsync(string entryName, ReadOnlyMemory<byte> key, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Entries.TryGetValue(entryName, out var wrapping)
                && Commitments.TryGetValue(entryName, out var stored)
                && CryptographicOperations.FixedTimeEquals(stored, HMACSHA256.HashData(wrapping, key.Span)));

        public ValueTask<byte[]> SealAsync(string entryName, ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> associatedData, CancellationToken cancellationToken)
        {
            if (!Available)
                throw new KekKeyringException("offline");
            if (!Entries.TryGetValue(entryName, out var wrapping))
                Entries[entryName] = wrapping = RandomNumberGenerator.GetBytes(32);
            Commitments[entryName] = HMACSHA256.HashData(wrapping, key.Span);
            var nonce = RandomNumberGenerator.GetBytes(12);
            var cipher = new byte[key.Length];
            var tag = new byte[16];
            using var aes = new AesGcm(wrapping, 16);
            aes.Encrypt(nonce, key.Span, cipher, tag, associatedData.Span);
            return ValueTask.FromResult<byte[]>([.. nonce, .. cipher, .. tag]);
        }

        public ValueTask<byte[]> OpenAsync(string entryName, ReadOnlyMemory<byte> sealedKey, ReadOnlyMemory<byte> associatedData, CancellationToken cancellationToken)
        {
            if (!Available || !Entries.TryGetValue(entryName, out var wrapping))
                throw new KekKeyringException("no entry");
            var span = sealedKey.Span;
            var plain = new byte[span.Length - 28];
            try
            {
                using var aes = new AesGcm(wrapping, 16);
                aes.Decrypt(span[..12], span[12..^16], span[^16..], plain, associatedData.Span);
            }
            catch (CryptographicException)
            {
                throw new KekKeyringException("bad tag");
            }

            if (CorruptOpen)
                plain[0] ^= 0xFF;
            return ValueTask.FromResult(plain);
        }
    }
}
