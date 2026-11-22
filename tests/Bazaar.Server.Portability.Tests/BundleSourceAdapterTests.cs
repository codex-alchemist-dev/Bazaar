using System.Security.Cryptography;

namespace Bazaar.Server.Portability.Tests;

public sealed class BundleSourceAdapterTests : IDisposable
{
    private static readonly DateTimeOffset _created = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);
    private static readonly byte[] _gitBundle = "BUNDLE-BYTES"u8.ToArray();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bazaar-sources-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public void Dispose()
    {
        _key.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class FakePersonalSource : IPersonalFieldSource
    {
        public Task<IReadOnlyList<PersonalSubjectState>> ListSubjectsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PersonalSubjectState>>(
                [new("alice", false), new("bob", true, new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero))]);

        public Task<IReadOnlyList<PersonalEnvelope>> ReadEnvelopesAsync(string subjectId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PersonalEnvelope>>([new("email", "key-1", [1, 2, 3], [9, 9, 9])]);
    }

    private sealed class NoDecryptor : IPersonalFieldDecryptor
    {
        public Task<string?> DecryptAsync(string subjectId, PersonalEnvelope envelope, CancellationToken ct) =>
            throw new InvalidOperationException("The ciphertext path must not decrypt.");
    }

    private sealed class Settings(bool allow) : IPersonalExportSettings
    {
        public Task<bool> AllowPlaintextPersonalAsync(CancellationToken ct) => Task.FromResult(allow);
    }

    private sealed class RecordingAudit : IPersonalExportAuditSink
    {
        public List<PersonalExportAuditRecord> Records { get; } = [];

        public Task AppendAsync(PersonalExportAuditRecord record, CancellationToken ct)
        {
            Records.Add(record);
            return Task.CompletedTask;
        }
    }

    private sealed class PlaintextDecryptor : IPersonalFieldDecryptor
    {
        public Task<string?> DecryptAsync(string subjectId, PersonalEnvelope envelope, CancellationToken ct) =>
            Task.FromResult<string?>("alice@example.test");
    }

    private static PersonalExportScope Owner(bool plaintext = false) =>
        new("owner", PersonalExportRole.Owner, IsOwnerSession: true, IncludePlaintext: plaintext);

    private static VaultBundleSource Vault(out FakeBundleWriter writer)
    {
        var git = new FakeVaultGit("abc123", new Dictionary<string, string>
        {
            ["notes/a.md"] = "# A\nsee [[b]]\n",
            ["notes/b.md"] = "# B\n",
        });
        writer = new FakeBundleWriter(_gitBundle);
        return new VaultBundleSource(new VaultExporter(git, writer), "/repo", "main");
    }

    private static PersonalFieldBundleSource Personal(bool plaintext = false, RecordingAudit? audit = null) =>
        new(new PersonalFieldExporter(
                new FakePersonalSource(),
                plaintext ? new PlaintextDecryptor() : new NoDecryptor(),
                new Settings(plaintext),
                audit ?? new RecordingAudit()),
            Owner(plaintext));

    private BundleAssemblyOptions Options(string name) => new()
    {
        InstanceId = "inst-1",
        CreatedAt = _created,
        SigningKey = _key,
        OutputDirectory = Path.Combine(_root, name),
    };

    private static Task<BundleAssemblyResult> Run(IEnumerable<IBundleSource> sources, BundleAssemblyOptions options) =>
        ExportBundleAssembler.AssembleAsync(sources, options, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Vault_source_places_exporter_files_under_vault_folder()
    {
        var source = Vault(out var writer);
        var result = await Run([source], Options("v"));

        Assert.Equal(
            ["vault/repo.bundle", "vault/tree/notes/a.md", "vault/tree/notes/b.md", "vault/vault-manifest.json"],
            result.Files.Select(f => f.Path).ToArray());
        Assert.Equal(["/repo"], writer.RepoPaths);
        Assert.Equal("abc123", source.Manifest!.HeadCommit);
        Assert.Equal(_gitBundle, File.ReadAllBytes(Path.Combine(result.Directory, "vault", "repo.bundle")));
        Assert.Equal("# A\nsee [[b]]\n", File.ReadAllText(Path.Combine(result.Directory, "vault", "tree", "notes", "a.md")));
    }

    [Fact]
    public async Task Vault_source_hashes_match_the_exporters_own_manifest()
    {
        var source = Vault(out _);
        var result = await Run([source], Options("v"));

        foreach (var entry in source.Manifest!.Files)
        {
            var bundled = result.Files.Single(f => f.Path == "vault/" + entry.Path);
            Assert.Equal(entry.Sha256, bundled.Sha256);
            Assert.Equal(entry.Bytes, bundled.Bytes);
        }
    }

    [Fact]
    public async Task Personal_source_writes_envelopes_tombstone_and_index()
    {
        var audit = new RecordingAudit();
        var source = Personal(audit: audit);
        var result = await Run([source], Options("p"));

        Assert.Equal(
            ["personal/alice.enc.jsonl", "personal/bob.tombstone.json", "personal/index.json"],
            result.Files.Select(f => f.Path).ToArray());
        Assert.Null(source.PlaintextNotice);
        Assert.Single(audit.Records);
        Assert.Contains("\"plaintextPersonal\":false", File.ReadAllText(Path.Combine(result.Directory, "personal", "index.json")));
    }

    [Fact]
    public async Task Personal_source_reports_plaintext_notice()
    {
        var source = Personal(plaintext: true);
        var result = await Run([source], Options("p"));

        Assert.Equal(PersonalFieldExporter.OwnerNotice, source.PlaintextNotice);
        Assert.Contains(result.Files, f => f.Path == "personal/alice.json");
    }

    [Fact]
    public async Task Personal_refusal_aborts_the_bundle_and_leaves_nothing()
    {
        var source = new PersonalFieldBundleSource(
            new PersonalFieldExporter(new FakePersonalSource(), new NoDecryptor(), new Settings(false), new RecordingAudit()),
            new PersonalExportScope("owner", PersonalExportRole.Owner, IsOwnerSession: true, IncludePlaintext: true));
        var options = Options("p");

        await Assert.ThrowsAsync<PersonalExportRefusedException>(() => Run([source], options));

        Assert.False(Directory.Exists(options.OutputDirectory));
    }

    [Fact]
    public async Task Audit_source_writes_entries_checkpoints_and_archive_marker()
    {
        var chain = AuditChainFixture.Build(10, 4);
        var source = new AuditBundleSource(new AuditExporter(chain, chain));
        var result = await Run([source], Options("a"));

        Assert.Equal(
            [AuditArchiveFormat.ArchivePath, AuditArchiveFormat.CheckpointsPath, AuditArchiveFormat.EntriesPath],
            result.Files.Select(f => f.Path).ToArray());
        Assert.True(source.SelfCheck!.IsValid);

        var dir = result.Directory;
        var replay = AuditArchiveVerifier.VerifyFiles(
            File.ReadAllBytes(Path.Combine(dir, "audit", "entries.jsonl")),
            File.ReadAllBytes(Path.Combine(dir, "audit", "checkpoints.jsonl")),
            File.ReadAllBytes(Path.Combine(dir, "audit", "archive.json")),
            chain);
        Assert.True(replay.IsValid);
    }

    [Fact]
    public async Task Audit_source_starts_from_the_given_checkpoint()
    {
        var chain = AuditChainFixture.Build(10, 4);
        var source = new AuditBundleSource(new AuditExporter(chain, chain), startCheckpointLastSeq: 4);
        var result = await Run([source], Options("a"));

        Assert.True(source.SelfCheck!.IsValid);
        Assert.Contains(AuditArchiveFormat.EntriesPath, result.Files.Select(f => f.Path));
    }

    [Fact]
    public async Task Audit_failure_aborts_the_bundle_and_leaves_nothing()
    {
        var chain = AuditChainFixture.Build(10, 4);
        var source = new AuditBundleSource(new AuditExporter(chain, chain), startCheckpointLastSeq: 999);
        var options = Options("a");

        await Assert.ThrowsAsync<AuditExportException>(() => Run([source], options));

        Assert.False(Directory.Exists(options.OutputDirectory));
    }

    [Fact]
    public async Task All_three_sources_assemble_into_one_verified_signed_bundle()
    {
        var chain = AuditChainFixture.Build(10, 4);
        IBundleSource[] sources =
        [
            new AuditBundleSource(new AuditExporter(chain, chain)),
            Personal(),
            Vault(out _),
        ];

        var result = await Run(sources, Options("all"));
        var bundle = await ExportBundleAssembler.ReadDirectoryAsync(result.Directory, TestContext.Current.CancellationToken);

        Assert.Empty(ExportBundleWriter.Verify(bundle));
        Assert.True(BundleSigner.Verify(bundle, _key.ExportSubjectPublicKeyInfo()));
        Assert.Equal(10, result.Files.Count);
        Assert.Equal(
            [
                "audit/archive.json", "audit/checkpoints.jsonl", "audit/entries.jsonl",
                "personal/alice.enc.jsonl", "personal/bob.tombstone.json", "personal/index.json",
                "vault/repo.bundle", "vault/tree/notes/a.md", "vault/tree/notes/b.md", "vault/vault-manifest.json",
            ],
            result.Files.Select(f => f.Path).ToArray());
    }

    [Fact]
    public async Task Bundle_from_all_sources_is_deterministic_whatever_the_registration_order()
    {
        var chain = AuditChainFixture.Build(10, 4);
        IBundleSource[] Make() => [new AuditBundleSource(new AuditExporter(chain, chain)), Vault(out _)];

        var one = await Run(Make(), Options("one"));
        var two = await Run(Make().Reverse(), Options("two"));

        Assert.Equal(one.Manifest, two.Manifest);
    }

    [Fact]
    public async Task Bundle_can_be_wrapped_as_encrypted_archive()
    {
        var options = Options("enc") with
        {
            EncryptedArchivePath = Path.Combine(_root, "bundle.enc"),
            Passphrase = "correct horse",
            KdfParameters = new Argon2Parameters(ExportBundleCipher.MinMemoryKiB, ExportBundleCipher.MinTimeCost, 1),
        };
        var result = await Run([Vault(out _)], options);

        Assert.True(File.Exists(result.EncryptedArchivePath));
        var plain = ExportBundleCipher.Decrypt(File.ReadAllBytes(result.EncryptedArchivePath!), "correct horse");
        Assert.Contains(plain.Files, f => f.Path == "vault/repo.bundle");
        Assert.Equal(result.Manifest, plain.Manifest);
        Assert.True(BundleSigner.Verify(plain, _key.ExportSubjectPublicKeyInfo()));
    }
}
