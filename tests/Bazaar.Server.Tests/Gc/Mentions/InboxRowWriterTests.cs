using Bazaar.Server.Gc.Mentions;
using Bazaar.Server.Gc.Model;

namespace Bazaar.Server.Tests.Gc.Mentions;

public sealed class InboxRowWriterTests(GcDb db) : IClassFixture<GcDb>
{
    private static readonly Guid _author = Guid.CreateVersion7();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private InboxRowWriter Writer => new(db.DataSource);

    private async Task<Guid> NewAgentAsync()
    {
        var id = Guid.CreateVersion7();
        await using var cmd = db.DataSource.CreateCommand("INSERT INTO agent (agent_id, name, state) VALUES ($1, 'inbox writer test', 'active')");
        cmd.Parameters.AddWithValue(id);
        await cmd.ExecuteNonQueryAsync(Ct);
        return id;
    }

    private async Task<long> CountAsync(Guid agent) =>
        await db.ScalarAsync<long>("SELECT count(*) FROM inbox_item WHERE agent_id = $1", agent);

    [Fact]
    public async Task Each_planned_recipient_gets_one_row()
    {
        GcTestSkip.WithoutDatabase();
        var ana = await NewAgentAsync();
        var bob = await NewAgentAsync();
        var gc = new GcRecord(await db.CreateGcAsync(), null, "general", null);
        var plan = InboxPlanner.PlanMentionInbox(
            gc,
            42,
            [new AcceptedMention(ana, 0, 4), new AcceptedMention(bob, 5, 4)],
            _author);

        var inserted = await Writer.WriteAsync(plan, Ct);

        Assert.Equal(2, inserted);
        Assert.Equal(1L, await CountAsync(ana));
        Assert.Equal(1L, await CountAsync(bob));
    }

    [Fact]
    public async Task Duplicate_write_of_the_same_plan_creates_no_extra_rows()
    {
        GcTestSkip.WithoutDatabase();
        var ana = await NewAgentAsync();
        var bob = await NewAgentAsync();
        var gc = new GcRecord(await db.CreateGcAsync(), null, "general", null);
        var plan = InboxPlanner.PlanMentionInbox(
            gc,
            43,
            [new AcceptedMention(ana, 0, 4), new AcceptedMention(bob, 5, 4)],
            _author);

        var first = await Writer.WriteAsync(plan, Ct);
        var second = await Writer.WriteAsync(plan, Ct);

        Assert.Equal(2, first);
        Assert.Equal(0, second);
        Assert.Equal(1L, await CountAsync(ana));
        Assert.Equal(1L, await CountAsync(bob));
    }

    [Fact]
    public async Task Empty_plan_writes_nothing()
    {
        GcTestSkip.WithoutDatabase();
        var ana = await NewAgentAsync();
        var gc = new GcRecord(await db.CreateGcAsync(), null, "general", null);
        var plan = InboxPlanner.PlanMentionInbox(gc, 44, [new AcceptedMention(_author, 0, 4)], _author);

        var inserted = await Writer.WriteAsync(plan, Ct);

        Assert.Equal(0, inserted);
        Assert.Equal(0L, await CountAsync(ana));
        Assert.Equal(0, await Writer.WriteAsync([], Ct));
    }

    [Fact]
    public async Task Row_carries_the_gc_id_and_the_message_seq()
    {
        GcTestSkip.WithoutDatabase();
        var ana = await NewAgentAsync();
        var gcId = await db.CreateGcAsync();
        var plan = InboxPlanner.PlanMentionInbox(
            new GcRecord(gcId, null, "general", null),
            4242,
            [new AcceptedMention(ana, 0, 4)],
            _author);

        await Writer.WriteAsync(plan, Ct);

        await using var cmd = db.DataSource.CreateCommand(
            "SELECT gc_id, gc_seq, kind, priority, state FROM inbox_item WHERE agent_id = $1");
        cmd.Parameters.AddWithValue(ana);
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        Assert.Equal(gcId, reader.GetGuid(0));
        Assert.Equal(4242L, reader.GetInt64(1));
        Assert.Equal("mention", reader.GetString(2));
        Assert.Equal((short)1, reader.GetInt16(3));
        Assert.Equal("pending", reader.GetString(4));
    }

    [Fact]
    public async Task Human_principals_without_an_agent_row_are_skipped()
    {
        GcTestSkip.WithoutDatabase();
        var human = Guid.CreateVersion7();
        var gc = new GcRecord(await db.CreateGcAsync(), null, "general", null);
        var plan = InboxPlanner.PlanMentionInbox(gc, 45, [new AcceptedMention(human, 0, 4)], _author);

        var inserted = await Writer.WriteAsync(plan, Ct);

        Assert.Equal(0, inserted);
        Assert.Equal(0L, await CountAsync(human));
    }

    [Fact]
    public async Task Rows_written_in_a_transaction_roll_back_with_it()
    {
        GcTestSkip.WithoutDatabase();
        var ana = await NewAgentAsync();
        var gc = new GcRecord(await db.CreateGcAsync(), null, "general", null);
        var plan = InboxPlanner.PlanMentionInbox(gc, 46, [new AcceptedMention(ana, 0, 4)], _author);

        await using (var conn = await db.DataSource.OpenConnectionAsync(Ct))
        await using (var tx = await conn.BeginTransactionAsync(Ct))
        {
            Assert.Equal(1, await Writer.WriteAsync(tx, plan, Ct));
            await tx.RollbackAsync(Ct);
        }

        Assert.Equal(0L, await CountAsync(ana));
    }
}
