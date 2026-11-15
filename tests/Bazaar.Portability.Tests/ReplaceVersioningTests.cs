using System.Text;
using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc.Edit;
using Bazaar.Server.Gc.Read;
using Bazaar.Server.Gc.Write;
using Bazaar.Server.Vault.Git;

namespace Bazaar.Portability.Tests;

public class ReplaceVersioningTests : IDisposable
{
    private static readonly Guid _gcId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid _author = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid _other = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private readonly string _checkout = Path.Combine(Path.GetTempPath(), "bazaar-replace-" + Guid.NewGuid().ToString("N"));

    public ReplaceVersioningTests() => Directory.CreateDirectory(_checkout);

    public void Dispose()
    {
        if (Directory.Exists(_checkout))
        {
            Directory.Delete(_checkout, recursive: true);
        }
        GC.SuppressFinalize(this);
    }

    private static SourceRecord Note(string key, string title, string body) =>
        new(key, "document", title, body, new Dictionary<string, string>());

    private static GcImportTarget GcTarget(InMemoryGcLog gc, Guid author, IGcMessageEditor? editor = null) =>
        new(gc, new PlainGcBodyCodec(), gc, _gcId, author, AuthorKind.Owner, new GcPrincipal(author, PrincipalKind.Owner, "owner"), editor);

    /// <summary>Stands in for the server's editor: accepts every edit with the next version, or refuses with a reason.</summary>
    private sealed class FakeEditor(InMemoryGcLog gc, string? refuse = null) : IGcMessageEditor
    {
        private readonly Dictionary<Guid, int> _versions = [];

        public int Calls { get; private set; }

        public async Task<GcMessageEditOutcome> EditAsync(GcMessageEdit request, CancellationToken ct)
        {
            Calls++;
            if (refuse is not null)
            {
                return new GcMessageEditRefused(refuse);
            }
            var next = (_versions.TryGetValue(request.MessageId, out var current) ? current : 1) + 1;
            _versions[request.MessageId] = next;
            var results = await gc.AppendManyAsync(
                [new AppendRequest(request.GcId, GcEventKinds.MessageEdit, AuthorKind.Owner, request.EditorId, SourceTrust.Owner, request.ClientMsgId)
                {
                    MessageId = request.MessageId,
                    Version = next,
                }],
                ct);
            return new GcMessageEditAccepted(next, results[0].Seq);
        }
    }

    [Fact]
    public async Task Gc_index_finds_an_imported_message_and_replace_appends_a_new_version()
    {
        var gc = new InMemoryGcLog();
        var target = GcTarget(gc, _author, new FakeEditor(gc));
        var index = new GcExistingObjectIndex(gc, _gcId, new GcPrincipal(_author, PrincipalKind.Owner, "owner"));
        await target.AppendBatchAsync("job-1", 0, [new ImportItem(Note("g1", "Plan", "first"), new ImportProvenance("job-1", "g1"))], CancellationToken.None);

        var before = Assert.Single(await index.FindAsync([("document", "Plan")], CancellationToken.None));
        Assert.Equal("1", before.Version);
        Assert.Equal(_author.ToString("D"), before.Owner);

        var replace = new ImportItem(Note("g1", "Plan", "second"), new ImportProvenance("job-2", "g1")) { Replaces = before };
        await target.AppendBatchAsync("job-2", 0, [replace], CancellationToken.None);

        var after = Assert.Single(await index.FindAsync([("document", "Plan")], CancellationToken.None));
        Assert.Equal("2", after.Version);
        Assert.Equal(before.ObjectId, after.ObjectId);
        // The original message is still in the log, and the edit sits after it.
        var events = new List<GcEvent>();
        await foreach (var ev in gc.ReadAsync(_gcId, 0, 100, new GcPrincipal(_author, PrincipalKind.Owner, "owner"), CancellationToken.None))
        {
            events.Add(ev);
        }
        Assert.Equal([GcEventKinds.Message, GcEventKinds.MessageEdit], events.Select(e => e.Kind).ToList());
        Assert.Equal(2, events[1].Version);
    }

    [Fact]
    public async Task Gc_replace_by_another_author_is_refused_and_writes_nothing()
    {
        var gc = new InMemoryGcLog();
        await GcTarget(gc, _author, new FakeEditor(gc)).AppendBatchAsync("job-1", 0, [new ImportItem(Note("g1", "Plan", "first"), new ImportProvenance("job-1", "g1"))], CancellationToken.None);
        var index = new GcExistingObjectIndex(gc, _gcId, new GcPrincipal(_author, PrincipalKind.Owner, "owner"));
        var stored = Assert.Single(await index.FindAsync([("document", "Plan")], CancellationToken.None));
        var before = gc.Count;

        var replace = new ImportItem(Note("g1", "Plan", "second"), new ImportProvenance("job-2", "g1")) { Replaces = stored };
        var editor = new FakeEditor(gc);
        await Assert.ThrowsAsync<InvalidOperationException>(() => GcTarget(gc, _other, editor).AppendBatchAsync("job-2", 0, [replace], CancellationToken.None));

        Assert.Equal(0, editor.Calls);
        Assert.Equal(before, gc.Count);
    }

    [Fact]
    public async Task Gc_replace_without_an_editor_is_refused_before_anything_is_written()
    {
        var gc = new InMemoryGcLog();
        await GcTarget(gc, _author, new FakeEditor(gc)).AppendBatchAsync("job-1", 0, [new ImportItem(Note("g1", "Plan", "first"), new ImportProvenance("job-1", "g1"))], CancellationToken.None);
        var stored = Assert.Single(await new GcExistingObjectIndex(gc, _gcId, new GcPrincipal(_author, PrincipalKind.Owner, "owner")).FindAsync([("document", "Plan")], CancellationToken.None));
        var before = gc.Count;

        var replace = new ImportItem(Note("g1", "Plan", "second"), new ImportProvenance("job-2", "g1")) { Replaces = stored };
        await Assert.ThrowsAsync<NotSupportedException>(() => GcTarget(gc, _author).AppendBatchAsync("job-2", 0, [replace], CancellationToken.None));

        Assert.Equal(before, gc.Count);
    }

    [Fact]
    public async Task Gc_replace_refused_by_the_editor_fails_the_batch_and_writes_nothing()
    {
        var gc = new InMemoryGcLog();
        await GcTarget(gc, _author, new FakeEditor(gc)).AppendBatchAsync("job-1", 0, [new ImportItem(Note("g1", "Plan", "first"), new ImportProvenance("job-1", "g1"))], CancellationToken.None);
        var stored = Assert.Single(await new GcExistingObjectIndex(gc, _gcId, new GcPrincipal(_author, PrincipalKind.Owner, "owner")).FindAsync([("document", "Plan")], CancellationToken.None));
        var before = gc.Count;

        var replace = new ImportItem(Note("g1", "Plan", "second"), new ImportProvenance("job-2", "g1")) { Replaces = stored };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            GcTarget(gc, _author, new FakeEditor(gc, "The edit window has closed.")).AppendBatchAsync("job-2", 0, [replace], CancellationToken.None));

        Assert.Contains("The edit window has closed.", ex.Message);
        Assert.Equal(before, gc.Count);
    }

    [Fact]
    public async Task Vault_replace_is_refused_when_the_note_changed_after_it_was_looked_up()
    {
        var git = new InMemoryVaultGit();
        var identity = new GitIdentity("Bazaar Import", "import@example.invalid");
        var target = new VaultImportTarget(git, _checkout, _checkout, identity);
        await target.WriteBatchAsync("job-1", 0, [new ImportItem(Note("v1", "Plan", "first"), new ImportProvenance("job-1", "v1"))], CancellationToken.None);
        var stored = Assert.Single(await new VaultExistingObjectIndex(git, _checkout).FindAsync([("document", "Plan")], CancellationToken.None));

        // Someone else edits the note after the lookup.
        var path = Path.Combine(_checkout, stored.Location!);
        await File.WriteAllTextAsync(path, "---\nid: " + stored.ObjectId + "\ntype: draft\ntitle: Plan\n---\nedited by someone else\n", new UTF8Encoding(false), TestContext.Current.CancellationToken);
        await git.CommitAsync(_checkout, "someone edits", identity, TestContext.Current.CancellationToken);
        var before = await File.ReadAllTextAsync(path, CancellationToken.None);

        var replace = new ImportItem(Note("v2", "Plan", "second"), new ImportProvenance("job-2", "v2")) { Replaces = stored };
        await Assert.ThrowsAsync<InvalidOperationException>(() => target.WriteBatchAsync("job-2", 0, [replace], CancellationToken.None));

        Assert.Equal(before, await File.ReadAllTextAsync(path, CancellationToken.None));
    }

    [Fact]
    public async Task Vault_replace_overwrites_in_place_and_keeps_the_earlier_content_in_history()
    {
        var git = new InMemoryVaultGit();
        var identity = new GitIdentity("Bazaar Import", "import@example.invalid");
        var target = new VaultImportTarget(git, _checkout, _checkout, identity);
        await target.WriteBatchAsync("job-1", 0, [new ImportItem(Note("v1", "Plan", "first"), new ImportProvenance("job-1", "v1"))], CancellationToken.None);
        var index = new VaultExistingObjectIndex(git, _checkout);
        var stored = Assert.Single(await index.FindAsync([("document", "Plan")], CancellationToken.None));
        Assert.Equal("imports/job-1/v1.md", stored.Location);
        var oldHead = stored.Version;

        var replace = new ImportItem(Note("v2", "Plan", "second"), new ImportProvenance("job-2", "v2")) { Replaces = stored };
        await target.WriteBatchAsync("job-2", 0, [replace], CancellationToken.None);

        var now = await File.ReadAllTextAsync(Path.Combine(_checkout, stored.Location!), CancellationToken.None);
        Assert.Contains("second", now);
        Assert.Contains("id: " + stored.ObjectId, now);
        var previous = await git.ReadTextAsync(_checkout, oldHead, stored.Location!, CancellationToken.None);
        Assert.Contains("first", previous);
        Assert.Equal(stored.ObjectId, Assert.Single(await new VaultExistingObjectIndex(git, _checkout).FindAsync([("document", "Plan")], CancellationToken.None)).ObjectId);
    }

    [Fact]
    public async Task Vault_read_keys_includes_a_replaced_note_for_the_job_that_replaced_it()
    {
        var git = new InMemoryVaultGit();
        var target = new VaultImportTarget(git, _checkout, _checkout, new GitIdentity("Bazaar Import", "import@example.invalid"));
        await target.WriteBatchAsync("job-1", 0, [new ImportItem(Note("v1", "Plan", "first"), new ImportProvenance("job-1", "v1"))], CancellationToken.None);
        var stored = Assert.Single(await new VaultExistingObjectIndex(git, _checkout).FindAsync([("document", "Plan")], CancellationToken.None));
        await target.WriteBatchAsync("job-2", 0, [new ImportItem(Note("v2", "Plan", "second"), new ImportProvenance("job-2", "v2")) { Replaces = stored }], CancellationToken.None);

        var keys = await target.ReadSourceKeysAsync("job-2", CancellationToken.None);

        Assert.Equal(["v2"], keys);
    }

    [Fact]
    public async Task Vault_replace_refuses_a_path_that_climbs_out_of_the_checkout()
    {
        var git = new InMemoryVaultGit();
        var target = new VaultImportTarget(git, _checkout, _checkout, new GitIdentity("Bazaar Import", "import@example.invalid"));
        var escape = new ExistingObject("document", "Plan", "obj-1", "rev-1") { Location = "../outside.md" };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            target.WriteBatchAsync("job-2", 0, [new ImportItem(Note("v2", "Plan", "x"), new ImportProvenance("job-2", "v2")) { Replaces = escape }], CancellationToken.None));
    }
}
