using System.Text;
using Bazaar.Core;
using Bazaar.Server.Identity.Kek;
using Bazaar.Server.SealedStorage;
using Bazaar.Server.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.SealedBootstrap.Tests;

/// <summary>Doc 61 work package 5: host bootstrap values in the sealed scope <c>host:bootstrap</c>.</summary>
public sealed class SealedBootstrapTests : IDisposable
{
    private const string _canary = "Host=db;Password=CANARY-bootstrap-do-not-leak-4412";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bazaar-sealed-bootstrap-" + Guid.NewGuid().ToString("N"));
    private readonly RecordingSink _audit = new();
    private readonly InMemorySealAnchor _anchor = new();

    public SealedBootstrapTests()
    {
        Directory.CreateDirectory(_dir);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(_dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string SealedRoot => Path.Combine(_dir, "sealed");

    private SealedBootstrapStore Bootstrap(SealedBootstrapOptions? options = null) =>
        new(new SealedStore(new SealedStoreOptions { RootPath = SealedRoot }, FileKekProvider.Open(Path.Combine(_dir, "keys", "kek.bin")), _anchor, _audit), _audit, options);

    private string PlainFile(string name, string content)
    {
        var path = Path.Combine(_dir, "plain", name);
        var directory = Path.GetDirectoryName(path)!;
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(directory);
        else
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task Values_round_trip_through_the_store_and_the_resolver()
    {
        using var store = Bootstrap();
        await store.SetAsync("database", _canary);

        Assert.Equal(_canary, await store.GetAsync("database"));
        Assert.Equal(_canary, await new SealedBootstrapSecretResolver(store).ResolveAsync("sealed:database", CancellationToken.None));
        Assert.Equal(["database"], await store.ListNamesAsync());

        using var reopened = Bootstrap();
        Assert.Equal(_canary, await reopened.GetAsync("database"));
        Assert.True(await reopened.RemoveAsync("database"));
        await Assert.ThrowsAsync<SealedBootstrapValueNotFoundException>(() => reopened.GetAsync("database").AsTask());
    }

    [Fact]
    public async Task The_canary_is_on_no_file_and_in_no_audit_entry()
    {
        using var store = Bootstrap();
        await store.SetAsync("database", _canary);
        await store.ImportPlaintextFileAsync("valkey", PlainFile("valkey.txt", _canary + "-2\n"));

        foreach (var file in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith("kek.bin", StringComparison.Ordinal)))
        {
            Assert.DoesNotContain("CANARY", Encoding.Latin1.GetString(File.ReadAllBytes(file)), StringComparison.Ordinal);
        }

        Assert.All(_audit.Events, e => Assert.DoesNotContain("CANARY", e.Detail + e.Path, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_tampered_value_fails_closed_with_no_data_and_an_audit_entry()
    {
        using (var store = Bootstrap())
        {
            await store.SetAsync("database", _canary);
        }

        foreach (var file in Directory.EnumerateFiles(SealedRoot, "*.bzs", SearchOption.AllDirectories))
        {
            var bytes = File.ReadAllBytes(file);
            bytes[^1] ^= 0x01;
            File.WriteAllBytes(file, bytes);
        }

        using var reopened = Bootstrap();
        var ex = await Assert.ThrowsAsync<SecretResolutionException>(() => new SealedBootstrapSecretResolver(reopened).ResolveAsync("sealed:database", CancellationToken.None).AsTask());
        Assert.IsType<SealTamperedException>(ex.InnerException);
        Assert.DoesNotContain("CANARY", ex.Message, StringComparison.Ordinal);
        Assert.Contains(_audit.Events, e => e.Kind == "seal.tamper");
    }

    [Fact]
    public async Task A_plaintext_value_written_over_the_sealed_file_fails_closed()
    {
        using (var store = Bootstrap())
        {
            await store.SetAsync("database", "old");
        }

        foreach (var file in Directory.EnumerateFiles(SealedRoot, "*.bzs", SearchOption.AllDirectories))
        {
            File.WriteAllText(file, _canary);
        }

        using var reopened = Bootstrap();
        await Assert.ThrowsAsync<SealTamperedException>(() => reopened.GetAsync("database").AsTask());
    }

    [Fact]
    public async Task A_different_KEK_cannot_read_the_scope()
    {
        using (var store = Bootstrap())
        {
            await store.SetAsync("database", _canary);
        }

        using var other = new SealedBootstrapStore(
            new SealedStore(new SealedStoreOptions { RootPath = SealedRoot }, FileKekProvider.Open(Path.Combine(_dir, "other", "kek.bin")), _anchor, _audit), _audit);
        await Assert.ThrowsAsync<SealTamperedException>(() => other.GetAsync("database").AsTask());
    }

    [Fact]
    public async Task Import_seals_verifies_and_then_wipes_the_plaintext()
    {
        var path = PlainFile("db.txt", _canary + "\r\n");
        using var store = Bootstrap();

        Assert.Equal(SealedBootstrapImportResult.Imported, await store.ImportPlaintextFileAsync("database", path));

        Assert.False(File.Exists(path));
        Assert.Equal(_canary, await store.GetAsync("database"));
        Assert.Contains(_audit.Events, e => e.Kind == "bootstrap.import" && e.Path == "values/database");
    }

    [Fact]
    public async Task A_half_done_import_with_the_same_value_only_wipes()
    {
        using var store = Bootstrap();
        await store.SetAsync("database", _canary);
        var path = PlainFile("db.txt", _canary + "\n");

        Assert.Equal(SealedBootstrapImportResult.AlreadyImported, await store.ImportPlaintextFileAsync("database", path));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Import_over_a_different_value_is_refused_and_keeps_the_plaintext()
    {
        using var store = Bootstrap();
        await store.SetAsync("database", "something else");
        var path = PlainFile("db.txt", _canary);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ImportPlaintextFileAsync("database", path).AsTask());

        Assert.Equal(_canary, File.ReadAllText(path));
        Assert.Equal("something else", await store.GetAsync("database"));
    }

    [Fact]
    public async Task Import_of_a_link_or_an_empty_file_is_refused()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating a symbolic link needs a privilege on Windows.");
        var target = PlainFile("target.txt", _canary);
        var link = Path.Combine(_dir, "plain", "link.txt");
        File.CreateSymbolicLink(link, target);
        using var store = Bootstrap();

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ImportPlaintextFileAsync("database", link).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => store.ImportPlaintextFileAsync("empty", PlainFile("empty.txt", "\n")).AsTask());

        Assert.True(File.Exists(target));
        Assert.Empty(await store.ListNamesAsync());
    }

    [Fact]
    public async Task Import_of_a_file_with_another_hard_link_is_refused()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Hard links are made with ln here.");
        var path = PlainFile("db.txt", _canary);
        var alias = Path.Combine(_dir, "plain", "alias.txt");
        using (var ln = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ln", [path, alias]) { UseShellExecute = false }))
        {
            ln!.WaitForExit();
            Assert.Equal(0, ln.ExitCode);
        }

        using var store = Bootstrap();
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ImportPlaintextFileAsync("database", path).AsTask());

        Assert.Equal(_canary, File.ReadAllText(path));
        Assert.Equal(_canary, File.ReadAllText(alias));
        Assert.Empty(await store.ListNamesAsync());
        Assert.Contains(_audit.Events, e => e.Kind == "bootstrap.import.refused");
    }

    [Fact]
    public async Task Import_from_a_directory_that_group_or_others_can_write_is_refused()
    {
        if (OperatingSystem.IsWindows())
            return; // POSIX modes only.
        var path = PlainFile("db.txt", _canary);
        var directory = Path.GetDirectoryName(path)!;
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupWrite);
        using var store = Bootstrap();

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ImportPlaintextFileAsync("database", path).AsTask());

        Assert.Equal(_canary, File.ReadAllText(path));
        Assert.Empty(await store.ListNamesAsync());

        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Assert.Equal(SealedBootstrapImportResult.Imported, await store.ImportPlaintextFileAsync("database", path));
    }

    [Fact]
    public async Task Import_reads_and_wipes_through_one_exclusive_handle()
    {
        var path = PlainFile("db.txt", _canary);
        using var store = Bootstrap();

        // While another handle holds the file open exclusively, the import cannot take its own handle, so nothing is sealed.
        await using (var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Assert.ThrowsAsync<IOException>(() => store.ImportPlaintextFileAsync("database", path).AsTask());
        }

        Assert.Empty(await store.ListNamesAsync());
        Assert.Equal(SealedBootstrapImportResult.Imported, await store.ImportPlaintextFileAsync("database", path));
    }

    [Fact]
    public async Task A_file_that_cannot_be_zero_filled_is_refused_before_anything_is_sealed()
    {
        if (OperatingSystem.IsWindows())
            return; // POSIX modes only.
        var path = PlainFile("db.txt", _canary);
        File.SetUnixFileMode(path, UnixFileMode.UserRead);
        using var store = Bootstrap();

        // Root can open anything for writing; the refusal is for ordinary accounts.
        if (Environment.UserName == "root")
            return;
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ImportPlaintextFileAsync("database", path).AsTask());
        Assert.Empty(await store.ListNamesAsync());
    }

    [Fact]
    public async Task Resolver_wraps_every_failure_but_cancellation()
    {
        var resolver = new SealedBootstrapSecretResolver(new ThrowingStore(new IOException("disk gone")));
        var ex = await Assert.ThrowsAsync<SecretResolutionException>(() => resolver.ResolveAsync("sealed:database", CancellationToken.None).AsTask());
        Assert.Equal("sealed:database", ex.Reference);
        Assert.IsType<IOException>(ex.InnerException);
        Assert.DoesNotContain("disk gone", ex.Message, StringComparison.Ordinal);

        var other = new SealedBootstrapSecretResolver(new ThrowingStore(new KekKeyringException("keyring down")));
        await Assert.ThrowsAsync<SecretResolutionException>(() => other.ResolveAsync("sealed:database", CancellationToken.None).AsTask());

        var cancelled = new SealedBootstrapSecretResolver(new ThrowingStore(new OperationCanceledException()));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.ResolveAsync("sealed:database", CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Configured_imports_run_before_the_first_read_and_are_ignored_once_done()
    {
        var path = PlainFile("db.txt", _canary);
        var options = new SealedBootstrapOptions { ImportFrom = new Dictionary<string, string> { ["database"] = path } };

        using (var store = Bootstrap(options))
        {
            Assert.Equal(_canary, await new SealedBootstrapSecretResolver(store).ResolveAsync("sealed:database", CancellationToken.None));
        }

        Assert.False(File.Exists(path));
        using var again = Bootstrap(options);
        Assert.Equal(_canary, await again.GetAsync("database"));
    }

    [Theory]
    [InlineData("kek")]
    [InlineData("kek.reference")]
    [InlineData("db.kek")]
    [InlineData("my-kek-path")]
    [InlineData("a_kek_b")]
    [InlineData("kekfile")]
    [InlineData("identity.kek_provider")]
    [InlineData("Database")]
    [InlineData("../x")]
    [InlineData("db\n")]
    [InlineData("")]
    public async Task Bad_and_KEK_names_are_refused(string name)
    {
        using var store = Bootstrap();

        await Assert.ThrowsAsync<ArgumentException>(() => store.SetAsync(name, "v").AsTask());
        await Assert.ThrowsAsync<SecretResolutionException>(() => new SealedBootstrapSecretResolver(store).ResolveAsync("sealed:" + name, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Resolver_handles_only_its_scheme_and_names_the_reference_on_a_miss()
    {
        using var store = Bootstrap();
        var resolver = new SealedBootstrapSecretResolver(store);

        Assert.True(resolver.CanResolve("sealed:database"));
        Assert.False(resolver.CanResolve("file:/x"));
        var ex = await Assert.ThrowsAsync<SecretResolutionException>(() => resolver.ResolveAsync("sealed:database", CancellationToken.None).AsTask());
        Assert.Equal("sealed:database", ex.Reference);
    }

    [Fact]
    public async Task Component_registers_the_store_and_resolver_and_runs_configured_imports()
    {
        var path = PlainFile("db.txt", _canary);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [KekComponent.DataRootKey] = _dir,
            [KekComponent.KekProviderKey] = "file",
            [SealedBootstrapComponent.ImportFromSection + ":database"] = path,
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<ISealAuditSink>(_audit);
        services.AddSingleton<IKekCustodyAuditSink>(new NullCustodyAudit());
        services.AddSingleton<IKekKeyring>(new OfflineKeyring());

        services.AddBazaarComponents(configuration, [new KekComponent(), new SealedStorageComponent(), new SealedBootstrapComponent()]);
        using var provider = services.BuildServiceProvider();

        var resolver = provider.GetRequiredService<SealedBootstrapSecretResolver>();
        Assert.Equal(_canary, await resolver.ResolveAsync("sealed:database", CancellationToken.None));
        Assert.False(File.Exists(path));
        Assert.Equal(["sealed-storage"], SealedBootstrapComponent.ManifestDefinition.DependsOn);
    }

    [Fact]
    public void Component_refuses_a_relative_or_KEK_import()
    {
        var services = new ServiceCollection();
        var relative = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [SealedBootstrapComponent.ImportFromSection + ":database"] = "db.txt" }).Build();
        var kek = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [SealedBootstrapComponent.ImportFromSection + ":kek"] = "/x" }).Build();

        Assert.Throws<InvalidOperationException>(() => new SealedBootstrapComponent().AddServices(services, relative));
        Assert.Throws<ArgumentException>(() => new SealedBootstrapComponent().AddServices(services, kek));
    }

    private sealed class ThrowingStore(Exception failure) : ISealedBootstrapStore
    {
        public ValueTask<string> GetAsync(string name, CancellationToken cancellationToken = default) => throw failure;

        public ValueTask SetAsync(string name, string value, CancellationToken cancellationToken = default) => throw failure;

        public ValueTask<bool> RemoveAsync(string name, CancellationToken cancellationToken = default) => throw failure;

        public ValueTask<IReadOnlyList<string>> ListNamesAsync(CancellationToken cancellationToken = default) => throw failure;

        public ValueTask<SealedBootstrapImportResult> ImportPlaintextFileAsync(string name, string path, CancellationToken cancellationToken = default) => throw failure;
    }

    private sealed class RecordingSink : ISealAuditSink
    {
        public List<SealAuditEvent> Events { get; } = [];

        public ValueTask RecordAsync(SealAuditEvent auditEvent, CancellationToken cancellationToken)
        {
            lock (Events)
            {
                Events.Add(auditEvent);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class NullCustodyAudit : IKekCustodyAuditSink
    {
        public ValueTask RecordAsync(KekCustodyAuditEvent auditEvent, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class OfflineKeyring : IKekKeyring
    {
        public string Kind => "offline";

        public ValueTask<KekKeyringProbe> ProbeAsync(string entryName, CancellationToken cancellationToken) =>
            ValueTask.FromResult(KekKeyringProbe.Unavailable("test"));

        public ValueTask<bool> VerifyCommitmentAsync(string entryName, ReadOnlyMemory<byte> key, CancellationToken cancellationToken) =>
            ValueTask.FromResult(false);

        public ValueTask<byte[]> SealAsync(string entryName, ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> associatedData, CancellationToken cancellationToken) =>
            throw new KekKeyringException("offline");

        public ValueTask<byte[]> OpenAsync(string entryName, ReadOnlyMemory<byte> sealedKey, ReadOnlyMemory<byte> associatedData, CancellationToken cancellationToken) =>
            throw new KekKeyringException("offline");
    }
}
