using System.Text;
using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc;
using Bazaar.Server.Gc.Body;
using Bazaar.Server.Gc.Read;
using Bazaar.Server.Gc.Write;

namespace Bazaar.Server.Tests.Gc;

/// <summary>An import withdrawal is a new flagged version written through the log writer; history is untouched (doc 01 Edits, doc 44 8.8, PROPOSED).</summary>
public sealed class GcWithdrawnVersionTests(GcDb db) : IClassFixture<GcDb>
{
    private static readonly Guid _author = Guid.CreateVersion7();

    private static MessageBody Body(string text) => new(Encoding.UTF8.GetBytes(text), PlaintextGcBodyCodec.PlaintextKeyId);

    [Fact]
    public async Task Withdrawal_adds_a_flagged_version_leaves_the_originals_and_the_view_hides_it()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var gc = await db.CreateGcAsync();
        var writer = new GcLogWriter(db.DataSource, new RecordingNotifier());
        await writer.AppendAsync(new AppendRequest(gc, GcEventKinds.Message, AuthorKind.Human, _author, SourceTrust.MemberHuman, "m-1") { Message = Body("imported text") }, ct);
        var messageId = await db.ScalarAsync<Guid>("SELECT message_id FROM gc_message WHERE gc_id = $1", gc);

        await writer.AppendAsync(new AppendRequest(gc, GcEventKinds.MessageEdit, AuthorKind.System, Guid.Empty, SourceTrust.System, "w-1")
        {
            MessageId = messageId,
            Version = 2,
            Message = Body("imported text"),
            WithdrawnByImport = "job-9",
        }, ct);

        Assert.Equal(2L, await db.ScalarAsync<long>("SELECT count(*) FROM gc_message_version WHERE gc_id = $1", gc));
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM gc_message_version WHERE gc_id = $1 AND version = 1 AND withdrawn_by_import IS NOT NULL", gc));
        Assert.Equal("job-9", await db.ScalarAsync<string>("SELECT withdrawn_by_import FROM gc_message_version WHERE gc_id = $1 AND version = 2", gc));
        Assert.Equal("imported text", Encoding.UTF8.GetString(await db.ScalarAsync<byte[]>("SELECT body_ct FROM gc_message_version WHERE gc_id = $1 AND version = 1", gc)));

        var view = await new PgGcStreamSource(db.DataSource, new PlaintextGcBodyCodec()).ReadTailAsync(gc, 10, ct);
        var message = Assert.Single(view);
        Assert.True(message.Withdrawn);
        Assert.True(message.Edited);
        Assert.Equal(GcBodyText.WithdrawnText, message.Text);
    }

    [Fact]
    public async Task An_unflagged_message_reads_normally()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var gc = await db.CreateGcAsync();
        var writer = new GcLogWriter(db.DataSource, new RecordingNotifier());
        await writer.AppendAsync(new AppendRequest(gc, GcEventKinds.Message, AuthorKind.Human, _author, SourceTrust.MemberHuman, "m-2") { Message = Body("hello") }, ct);

        var message = Assert.Single(await new PgGcStreamSource(db.DataSource, new PlaintextGcBodyCodec()).ReadTailAsync(gc, 10, ct));
        Assert.False(message.Withdrawn);
        Assert.Equal("hello", message.Text);
    }
}
