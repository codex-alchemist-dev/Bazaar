using System.Text;
using Bazaar.Contracts.Gc;
using Bazaar.Server.Vault.Git;

namespace Bazaar.Portability.Tests;

public class ImportTargetWriterTests : IDisposable
{
    private const string _jobId = "job-1";
    private const string _hash = "mapping-hash-1";
    private static readonly Guid _gcId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid _author = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly string _checkout = Path.Combine(Path.GetTempPath(), "bazaar-portability-" + Guid.NewGuid().ToString("N"));

    public ImportTargetWriterTests() => Directory.CreateDirectory(_checkout);

    public void Dispose()
    {
        if (Directory.Exists(_checkout))
        {
            Directory.Delete(_checkout, recursive: true);
        }
        GC.SuppressFinalize(this);
    }

    private static SourceRecord Vault(string key, string body = "body") =>
        new(key, "document", key, body, new Dictionary<string, string>());

    private static SourceRecord Gc(string key, string body) =>
        new(key, ImportCommitter.GcEntryKind, key, body, new Dictionary<string, string>());

    private static ImportPipelineResult Ready(params SourceRecord[] records) =>
        new(ImportStatus.Ready, "bazaar.import.a", [], new ImportPreview(records, []), _hash);

    private sealed record Harness(ImportTargetWriter Writer, InMemoryGcLog Gc, InMemoryVaultGit Git);

    private Harness Build()
    {
        var gc = new InMemoryGcLog();
        var git = new InMemoryVaultGit();
        var identity = new GitIdentity("Bazaar Import", "import@example.invalid");
        var viewer = new GcPrincipal(_author, PrincipalKind.Owner, "owner");
        var gcTarget = new GcImportTarget(gc, new PlainGcBodyCodec(), gc, _gcId, _author, AuthorKind.Owner, viewer);
        var vaultTarget = new VaultImportTarget(git, _checkout, _checkout, identity);
        return new Harness(new ImportTargetWriter(gcTarget, vaultTarget), gc, git);
    }

    [Fact]
    public async Task Round_trip_through_the_fakes_verifies_clean()
    {
        var harness = Build();
        var audit = new InMemoryAuditSink();
        var dryRun = Ready(Vault("v1", "first note"), Vault("v2", "second note"), Gc("g1", "a chat line"));

        var commit = await ImportCommitter.CommitAsync(dryRun, _hash, harness.Writer, audit, _jobId, 10, CancellationToken.None);
        var problems = await ImportVerifier.VerifyAsync(harness.Writer, dryRun, commit, _jobId, CancellationToken.None);

        Assert.True(commit.Succeeded);
        Assert.Equal(2, commit.BatchCount);
        Assert.Empty(problems);
        Assert.Equal(2, audit.Records.Count);
        Assert.Equal(1, harness.Gc.Count);
        Assert.Equal(["g1"], await harness.Writer.ReadGcSourceKeysAsync(_jobId, CancellationToken.None));
        Assert.Equal(["v1", "v2"], await harness.Writer.ReadVaultSourceKeysAsync(_jobId, CancellationToken.None));
        Assert.Equal(
            "first note",
            ImportBodyOf(Path.Combine(_checkout, "imports", _jobId, "v1.md")));
    }

    [Fact]
    public async Task Rerun_after_success_writes_nothing_new()
    {
        var harness = Build();
        var audit = new InMemoryAuditSink();
        var dryRun = Ready(Vault("v1"), Gc("g1", "line"));

        var first = await ImportCommitter.CommitAsync(dryRun, _hash, harness.Writer, audit, _jobId, 10, CancellationToken.None);
        var second = await ImportCommitter.CommitAsync(dryRun, _hash, harness.Writer, audit, _jobId, 10, CancellationToken.None);

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.Equal(0, second.BatchCount);
        Assert.Equal(1, harness.Gc.Count);
        Assert.Equal(1, harness.Git.CommitCount);
    }

    [Fact]
    public async Task Failed_gc_append_stops_the_job_at_the_gc_batch()
    {
        var harness = Build();
        harness.Gc.FailAppends = true;
        var audit = new InMemoryAuditSink();
        var dryRun = Ready(Gc("g1", "line"), Vault("v1"));

        var commit = await ImportCommitter.CommitAsync(dryRun, _hash, harness.Writer, audit, _jobId, 10, CancellationToken.None);

        Assert.False(commit.Succeeded);
        Assert.NotNull(commit.FirstFailure);
        Assert.Equal(0, commit.FirstFailure.BatchIndex);
        Assert.Equal(ImportTargetKind.GcMessage, commit.FirstFailure.Target);
        Assert.Equal(0, harness.Gc.Count);
        Assert.Empty(audit.Records);
    }

    [Fact]
    public async Task Verify_catches_a_missing_record()
    {
        var harness = Build();
        var audit = new InMemoryAuditSink();
        var dryRun = Ready(Vault("v1"), Vault("v2"));
        var lossy = new LossyImportTargetWriter(harness.Writer, dropKey: "v2");

        var commit = await ImportCommitter.CommitAsync(dryRun, _hash, lossy, audit, _jobId, 10, CancellationToken.None);
        var problems = await ImportVerifier.VerifyAsync(harness.Writer, dryRun, commit, _jobId, CancellationToken.None);

        Assert.True(commit.Succeeded);
        Assert.Contains("missing vault record v2", problems);
        Assert.DoesNotContain("missing vault record v1", problems);
    }

    [Fact]
    public void Front_matter_is_rendered_byte_for_byte()
    {
        var record = Vault("k1", "Hello\n");
        var item = new ImportItem(record with { Title = "Auth: service #1" }, new ImportProvenance(_jobId, "k1"));
        var id = VaultMarkdown.UlidFor(_jobId, "k1");

        var text = VaultMarkdown.Render(item);

        Assert.Equal(
            "---\nid: " + id + "\ntype: draft\ntitle: Auth service 1\nsource_trust: external\nimported_job: job-1\nsource_key: k1\n---\nHello\n",
            text);
        Assert.Matches("^[0-9A-HJKMNP-TV-Z]{26}$", id);
        Assert.Equal("k1", VaultMarkdown.ReadSourceKey(text));
    }

    [Fact]
    public void Vault_paths_reject_keys_that_could_escape_the_import_folder()
    {
        Assert.Equal("imports/job-1/k1.md", VaultImportTarget.PathFor(_jobId, "k1"));
        Assert.Throws<ArgumentException>(() => VaultImportTarget.PathFor(_jobId, "../escape"));
        Assert.Throws<ArgumentException>(() => VaultImportTarget.PathFor(_jobId, "a/b"));
    }

    private static string ImportBodyOf(string path)
    {
        var text = File.ReadAllText(path, Encoding.UTF8);
        var end = text.IndexOf("\n---\n", 4, StringComparison.Ordinal) + "\n---\n".Length;
        return text[end..].TrimEnd('\n');
    }
}
