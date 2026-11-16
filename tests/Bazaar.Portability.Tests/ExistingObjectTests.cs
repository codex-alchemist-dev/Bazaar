using System.Text;
using Bazaar.Server.Vault.Git;

namespace Bazaar.Portability.Tests;

public class ExistingObjectTests
{
    private const string _hash = "mapping-hash-1";
    private const string _jobId = "job-1";

    private static SourceRecord Note(string key, string title) =>
        new(key, "document", title, "body", new Dictionary<string, string>());

    [Fact]
    public void Find_matches_incoming_records_to_existing_objects_by_kind_and_title()
    {
        var preview = new ImportPreview([Note("a", "Plan"), Note("b", "Other"), new("c", "card", "Plan", "x", new Dictionary<string, string>())], []);
        var existing = new[] { new ExistingObject("document", "Plan", "obj-1", "rev-9") };

        var found = ExistingCollisions.Find(preview, existing);

        var match = Assert.Single(found);
        Assert.Equal("a", match.Incoming.SourceKey);
        Assert.Equal("obj-1", match.Existing.ObjectId);
    }

    [Fact]
    public async Task Replace_is_audited_with_the_replaced_objects_version()
    {
        var records = new[] { Note("a", "Plan"), Note("b", "Other") };
        var dryRun = new ImportPipelineResult(ImportStatus.Ready, "bazaar.import.a", [], new ImportPreview(records, [])
        {
            ReplaceSourceKeys = ["a"],
            ExistingCollisions = [new ExistingCollision(records[0], new ExistingObject("document", "Plan", "obj-1", "rev-9"))],
        }, _hash);
        var writer = new InMemoryImportTargetWriter();
        var audit = new InMemoryAuditSink();

        var result = await ImportCommitter.CommitAsync(dryRun, _hash, writer, audit, _jobId, 10, CancellationToken.None);

        Assert.True(result.Succeeded);
        var replace = Assert.Single(audit.Records, r => r.Kind == ImportAuditRecord.ReplaceKind);
        Assert.Equal("a", replace.SourceKey);
        Assert.Equal("obj-1", replace.ReplacedObjectId);
        Assert.Equal("rev-9", replace.ReplacedVersion);
        Assert.Single(audit.Records, r => r.Kind == ImportAuditRecord.BatchKind);
    }

    [Fact]
    public async Task A_replace_without_a_matching_existing_object_is_not_audited_as_a_replace()
    {
        var records = new[] { Note("a", "Plan") };
        var dryRun = new ImportPipelineResult(ImportStatus.Ready, "bazaar.import.a", [], new ImportPreview(records, [])
        {
            ReplaceSourceKeys = ["a"],
        }, _hash);
        var audit = new InMemoryAuditSink();

        await ImportCommitter.CommitAsync(dryRun, _hash, new InMemoryImportTargetWriter(), audit, _jobId, 10, CancellationToken.None);

        Assert.DoesNotContain(audit.Records, r => r.Kind == ImportAuditRecord.ReplaceKind);
    }

    [Fact]
    public async Task Vault_index_finds_notes_by_title_at_head()
    {
        var git = new InMemoryVaultGit();
        var checkout = Path.Combine(Path.GetTempPath(), "bazaar-index-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(checkout);
        try
        {
            File.WriteAllText(Path.Combine(checkout, "plan.md"), "---\nid: 01ABC\ntype: draft\ntitle: Plan\n---\nbody\n", new UTF8Encoding(false));
            await git.CommitAsync(checkout, "seed", new GitIdentity("Test", "test@example.invalid"), CancellationToken.None);
            var index = new VaultExistingObjectIndex(git, checkout);

            var found = await index.FindAsync([("document", "Plan"), ("document", "Missing")], CancellationToken.None);

            var match = Assert.Single(found);
            Assert.Equal("01ABC", match.ObjectId);
            Assert.Equal("Plan", match.Title);
            Assert.Equal("document", match.Kind);
        }
        finally
        {
            Directory.Delete(checkout, recursive: true);
        }
    }
}
