using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc;
using Bazaar.Server.Gc.Archive;
using Bazaar.Server.Gc.Body;
using Bazaar.Server.Gc.Create;
using Bazaar.Server.Gc.Write;

namespace Bazaar.Server.Tests.Gc.Archive;

/// <summary>The GC side of cold storage (doc 25 "Archive cold storage", doc 40 section 5a): text, Thinking flag, archive switch, project listing.</summary>
public sealed class GcArchiveBridgeTests(GcDb db) : IClassFixture<GcDb>
{
    private static readonly GcPrincipal _owner = new(Guid.Parse("aaaaaaaa-aaaa-7aaa-8aaa-aaaaaaaaaaaa"), PrincipalKind.Owner, "Owner");

    private sealed class ErasedCodec : IGcBodyCodec
    {
        public ValueTask<MessageBody> SealAsync(Guid authorId, string text, CancellationToken ct) => throw new NotSupportedException();

        public ValueTask<string?> OpenAsync(ReadOnlyMemory<byte> ciphertext, Guid keyId, ReadOnlyMemory<byte> nonce, CancellationToken ct) =>
            ValueTask.FromResult<string?>(null);
    }

    private GcLogWriter Writer() => new(db.DataSource, new RecordingNotifier());

    private async Task<Guid> NewGcAsync(string type, string title, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        Assert.True(await new GcCreateService(db.DataSource).CreateAsync(id, type, title, _owner, null, ct));
        return id;
    }

    private async Task<(Guid MessageId, int Version)> PostAsync(Guid gc, string text, CancellationToken ct)
    {
        var body = await new PlaintextGcBodyCodec().SealAsync(_owner.Id, text, ct);
        var key = Guid.NewGuid().ToString("D");
        var request = new AppendRequest(gc, GcEventKinds.Message, AuthorKind.Owner, _owner.Id, SourceTrust.Owner, key) { Message = body };
        var result = await Writer().AppendAsync(request, ct);
        var again = await Writer().AppendAsync(request, ct);
        Assert.NotNull(result.MessageId);
        Assert.True(again.Duplicate);
        Assert.Equal(result.MessageId, again.MessageId);
        return (result.MessageId.Value, 1);
    }

    [Fact]
    public async Task Message_text_is_opened_through_the_codec_and_an_erased_body_reads_as_null()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var gc = await NewGcAsync("general", "Plans", ct);
        var (messageId, version) = await PostAsync(gc, "ship it", ct);

        var bridge = new PgGcArchiveBridge(db.DataSource, Writer(), new PlaintextGcBodyCodec());
        Assert.IsAssignableFrom<Bazaar.Server.Gc.Read.IGcMessageTextSource>(bridge);
        Assert.Equal("ship it", await bridge.GetMessageTextAsync(gc, messageId, version, ct));
        Assert.Null(await bridge.GetMessageTextAsync(gc, Guid.NewGuid(), 1, ct));

        var erased = new PgGcArchiveBridge(db.DataSource, Writer(), new ErasedCodec());
        Assert.Null(await erased.GetMessageTextAsync(gc, messageId, version, ct));
    }

    [Fact]
    public async Task A_thinking_gc_is_flagged_a_normal_one_is_not_and_an_unknown_one_is_refused()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var bridge = new PgGcArchiveBridge(db.DataSource, Writer(), new PlaintextGcBodyCodec());

        Assert.True(await bridge.IsThinkingGcAsync(await NewGcAsync("thinking", "Thoughts", ct), ct));
        Assert.False(await bridge.IsThinkingGcAsync(await NewGcAsync("general", "Plans", ct), ct));
        await Assert.ThrowsAsync<GcNotFoundException>(() => bridge.IsThinkingGcAsync(Guid.NewGuid(), ct));
    }

    [Fact]
    public async Task Marking_archived_writes_one_system_event_stamps_the_gc_and_a_repeat_changes_nothing()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var gc = await NewGcAsync("general", "Plans", ct);
        await PostAsync(gc, "done", ct);
        var bridge = new PgGcArchiveBridge(db.DataSource, Writer(), new PlaintextGcBodyCodec());

        await bridge.MarkGcArchivedAsync(gc, "abc123", ct);
        await bridge.MarkGcArchivedAsync(gc, "abc123", ct);
        await bridge.MarkGcArchivedAsync(gc, "def456", ct); // already archived: left as it is

        await using var cmd = db.DataSource.CreateCommand(
            "SELECT (SELECT archived_at IS NOT NULL FROM gc WHERE gc_id = $1), (SELECT count(*) FROM gc_event WHERE gc_id = $1 AND kind = 'system' AND payload ->> 'action' = 'archived'), (SELECT count(*) FROM gc_message_version WHERE gc_id = $1)");
        cmd.Parameters.AddWithValue(gc);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        Assert.True(await r.ReadAsync(ct));
        Assert.True(r.GetBoolean(0));
        Assert.Equal(1L, r.GetInt64(1));
        Assert.Equal(1L, r.GetInt64(2));
    }

    [Fact]
    public async Task A_project_lists_its_gcs_project_first_and_leaves_out_thinking_gcs()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var project = await NewGcAsync("project", "Project", ct);
        var child = await NewGcAsync("general", "Child", ct);
        var thinking = await NewGcAsync("thinking", "Thoughts", ct);
        var other = await NewGcAsync("general", "Elsewhere", ct);
        await using (var cmd = db.DataSource.CreateCommand("UPDATE gc SET parent_gc_id = $2 WHERE gc_id = $1"))
        {
            cmd.Parameters.AddWithValue(child);
            cmd.Parameters.AddWithValue(project);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using (var cmd = db.DataSource.CreateCommand("UPDATE gc SET parent_gc_id = $2 WHERE gc_id = $1"))
        {
            cmd.Parameters.AddWithValue(thinking);
            cmd.Parameters.AddWithValue(child);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var ids = await new PgProjectGcLister(db.DataSource).ListGcIdsAsync(project.ToString("D"), ct);

        Assert.Equal([project, child], ids);
        Assert.DoesNotContain(other, ids);
        await Assert.ThrowsAsync<ArgumentException>(() => new PgProjectGcLister(db.DataSource).ListGcIdsAsync("not-a-guid", ct));
    }
}
