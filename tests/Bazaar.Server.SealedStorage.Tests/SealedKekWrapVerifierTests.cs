using System.Security.Cryptography;
using Bazaar.Server.Identity.Kek;

namespace Bazaar.Server.SealedStorage.Tests;

public sealed class SealedKekWrapVerifierTests : IDisposable
{
    private const string _scope = "host:bootstrap";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bazaar-verifier-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _disposables = [];

    public SealedKekWrapVerifierTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var d in _disposables)
            d.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private string Root => Path.Combine(_dir, "sealed");

    private string KeyPath => Path.Combine(_dir, "keys", "kek.bin");

    private FileKekProvider Kek(string name)
    {
        var kek = FileKekProvider.Open(Path.Combine(_dir, name));
        _disposables.Add(kek);
        return kek;
    }

    private SealedKekWrapVerifier Verifier(params string[] scopes) => new(Root, [new Names(scopes)]);

    private async Task CreateScopeAsync(IKekProvider kek, string scope)
    {
        var store = new SealedStore(new SealedStoreOptions { RootPath = Root }, kek, new InMemorySealAnchor(), new NullSink());
        using var opened = await store.OpenScopeAsync(scope, TestContext.Current.CancellationToken);
    }

    private string HeaderPath() => Directory.GetFiles(Root, "scope.key", SearchOption.AllDirectories).Single();

    [Fact]
    public async Task Verified_when_a_header_unwraps_under_the_candidate()
    {
        var kek = Kek("a.key");
        await CreateScopeAsync(kek, _scope);

        Assert.Equal(KekWrapCheck.Verified, await Verifier(_scope).CheckAsync(kek, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Failed_when_headers_exist_and_none_unwraps()
    {
        await CreateScopeAsync(Kek("a.key"), _scope);

        Assert.Equal(KekWrapCheck.Failed, await Verifier(_scope).CheckAsync(Kek("other.key"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Unknown_when_no_header_exists()
    {
        var kek = Kek("a.key");
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(KekWrapCheck.NoWrapsFound, await Verifier(_scope).CheckAsync(kek, ct));

        Directory.CreateDirectory(Path.Combine(Root, "emptyfolder"));
        Assert.Equal(KekWrapCheck.NoWrapsFound, await Verifier(_scope).CheckAsync(kek, ct));
    }

    [Fact]
    public async Task A_tampered_or_malformed_header_never_verifies()
    {
        var kek = Kek("a.key");
        await CreateScopeAsync(kek, _scope);
        var path = HeaderPath();
        var ct = TestContext.Current.CancellationToken;
        var original = await File.ReadAllBytesAsync(path, ct);

        var flipped = (byte[])original.Clone();
        flipped[^3] ^= 0x01;
        await File.WriteAllBytesAsync(path, flipped, ct);
        Assert.Equal(KekWrapCheck.Failed, await Verifier(_scope).CheckAsync(kek, ct));

        await File.WriteAllBytesAsync(path, original[..8], ct);
        Assert.Equal(KekWrapCheck.Failed, await Verifier(_scope).CheckAsync(kek, ct));

        await File.WriteAllBytesAsync(path, RandomNumberGenerator.GetBytes(10_000), ct);
        Assert.Equal(KekWrapCheck.Failed, await Verifier(_scope).CheckAsync(kek, ct));

        await File.WriteAllBytesAsync(path, [], ct);
        Assert.Equal(KekWrapCheck.Failed, await Verifier(_scope).CheckAsync(kek, ct));

        // Untouched header verifies again.
        await File.WriteAllBytesAsync(path, original, ct);
        Assert.Equal(KekWrapCheck.Verified, await Verifier(_scope).CheckAsync(kek, ct));
    }

    [Fact]
    public async Task A_header_moved_to_another_scope_folder_does_not_verify()
    {
        var kek = Kek("a.key");
        await CreateScopeAsync(kek, _scope);
        var source = HeaderPath();
        var target = Path.Combine(Root, Convert.ToHexString(SHA256.HashData("host:other"u8))[..32]);
        Directory.CreateDirectory(target);
        File.Move(source, Path.Combine(target, "scope.key"));

        Assert.Equal(KekWrapCheck.Failed, await Verifier("host:other").CheckAsync(kek, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_scope_nobody_names_is_not_trusted_but_a_named_one_still_verifies()
    {
        var kek = Kek("a.key");
        await CreateScopeAsync(kek, "soul:unlisted");
        var ct = TestContext.Current.CancellationToken;

        // Headers exist but none can be tested: fail closed, not "unknown".
        Assert.Equal(KekWrapCheck.Failed, await Verifier(_scope).CheckAsync(kek, ct));

        await CreateScopeAsync(kek, _scope);
        Assert.Equal(KekWrapCheck.Verified, await Verifier(_scope).CheckAsync(kek, ct));
    }

    [Fact]
    public async Task The_migration_wipes_the_premigrate_copy_only_when_the_sealed_key_verifies()
    {
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(Path.GetDirectoryName(KeyPath)!);
        var plain = FileKekProvider.Open(KeyPath);
        await CreateScopeAsync(plain, _scope);
        var premigrate = KeyPath + ".premigrate";
        var audit = new Audit();
        var keyring = new MemoryKeyring();
        var verifier = Verifier(_scope);

        // Existing data was wrapped under a different key: refused before anything is sealed or copied.
        var wrongKeyPath = Path.Combine(_dir, "wrong", "kek.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(wrongKeyPath)!);
        FileKekProvider.Open(wrongKeyPath).Dispose();
        await Assert.ThrowsAsync<KekKeyringException>(() =>
            FileKekProvider.OpenAsync(wrongKeyPath, new MemoryKeyring(), KekKeyringMode.Auto, audit, wrapVerifier: verifier, cancellationToken: ct).AsTask());
        Assert.False(File.Exists(wrongKeyPath + ".premigrate"));

        // The right key migrates, the sealed key unwraps the existing header, and the copy is wiped.
        plain.Dispose();
        (await FileKekProvider.OpenAsync(KeyPath, keyring, KekKeyringMode.Auto, audit, wrapVerifier: verifier, cancellationToken: ct)).Dispose();
        Assert.False(File.Exists(premigrate));
        Assert.Contains("kek.keyfile.premigrate_removed", audit.Kinds);
        Assert.DoesNotContain("kek.keyfile.premigrate_kept", audit.Kinds);

        // The migrated key still opens the existing scope.
        using var reopened = await FileKekProvider.OpenAsync(KeyPath, keyring, KekKeyringMode.Auto, audit, wrapVerifier: verifier, cancellationToken: ct);
        await CreateScopeAsync(reopened, _scope);
    }

    [Fact]
    public async Task Without_a_verifier_the_premigrate_copy_is_kept()
    {
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(Path.GetDirectoryName(KeyPath)!);
        FileKekProvider.Open(KeyPath).Dispose();
        var audit = new Audit();

        (await FileKekProvider.OpenAsync(KeyPath, new MemoryKeyring(), KekKeyringMode.Auto, audit, cancellationToken: ct)).Dispose();

        Assert.True(File.Exists(KeyPath + ".premigrate"));
        Assert.Contains("kek.keyfile.premigrate_kept", audit.Kinds);
    }

    private sealed class Names(string[] scopes) : ISealedScopeNameSource
    {
        public ValueTask<IReadOnlyCollection<string>> ListScopeNamesAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyCollection<string>>(scopes);
    }

    private sealed class NullSink : ISealAuditSink
    {
        public ValueTask RecordAsync(SealAuditEvent auditEvent, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class Audit : IKekCustodyAuditSink
    {
        private readonly List<string> _kinds = [];

        public IReadOnlyList<string> Kinds => _kinds;

        public ValueTask RecordAsync(KekCustodyAuditEvent auditEvent, CancellationToken cancellationToken)
        {
            _kinds.Add(auditEvent.Kind);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>An in-memory keyring with named entries, like the Secret Service.</summary>
    private sealed class MemoryKeyring : IKekKeyring
    {
        private readonly Dictionary<string, byte[]> _entries = [];
        private readonly Dictionary<string, byte[]> _commitments = [];

        public string Kind => "memory";

        public ValueTask<KekKeyringProbe> ProbeAsync(string entryName, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new KekKeyringProbe(true, _entries.ContainsKey(entryName), null));

        public ValueTask<bool> VerifyCommitmentAsync(string entryName, ReadOnlyMemory<byte> key, CancellationToken cancellationToken) =>
            ValueTask.FromResult(_entries.TryGetValue(entryName, out var wrapping)
                && _commitments.TryGetValue(entryName, out var stored)
                && CryptographicOperations.FixedTimeEquals(stored, HMACSHA256.HashData(wrapping, key.Span)));

        public ValueTask<byte[]> SealAsync(string entryName, ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> associatedData, CancellationToken cancellationToken)
        {
            if (!_entries.TryGetValue(entryName, out var wrapping))
                _entries[entryName] = wrapping = RandomNumberGenerator.GetBytes(32);
            _commitments[entryName] = HMACSHA256.HashData(wrapping, key.Span);
            var nonce = RandomNumberGenerator.GetBytes(12);
            var cipher = new byte[key.Length];
            var tag = new byte[16];
            using var aes = new AesGcm(wrapping, 16);
            aes.Encrypt(nonce, key.Span, cipher, tag, associatedData.Span);
            return ValueTask.FromResult<byte[]>([.. nonce, .. cipher, .. tag]);
        }

        public ValueTask<byte[]> OpenAsync(string entryName, ReadOnlyMemory<byte> sealedKey, ReadOnlyMemory<byte> associatedData, CancellationToken cancellationToken)
        {
            if (!_entries.TryGetValue(entryName, out var wrapping))
                throw new KekKeyringException("no entry");
            var span = sealedKey.Span;
            var plain = new byte[span.Length - 28];
            using var aes = new AesGcm(wrapping, 16);
            try
            {
                aes.Decrypt(span[..12], span[12..^16], span[^16..], plain, associatedData.Span);
            }
            catch (CryptographicException ex)
            {
                throw new KekKeyringException("bad tag", ex);
            }

            return ValueTask.FromResult(plain);
        }
    }
}
