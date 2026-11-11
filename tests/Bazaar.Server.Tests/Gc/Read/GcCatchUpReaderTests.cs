using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc.Read;
using Bazaar.Server.Tests.Gc.Consumers;
using Npgsql;

namespace Bazaar.Server.Tests.Gc.Read;

/// <summary>Tests for <see cref="GcCatchUpReader"/>: visibility, own events, mentions and the read position.</summary>
public sealed class GcCatchUpReaderTests(GcDb db) : IClassFixture<GcDb>
{
    private static readonly Guid _me = Guid.CreateVersion7();
    private static readonly Guid _other = Guid.CreateVersion7();

    private GcCatchUpReader CreateReader() => new(db.DataSource);

    private async Task AddMemberAsync(Guid gc, Guid principal, long joinedSeq = 0, long? leftSeq = null)
    {
        await using var cmd = db.DataSource.CreateCommand(
            "INSERT INTO gc_member (gc_id, principal_id, role, joined_seq, left_seq) VALUES ($1, $2, 'member', $3, $4)");
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gc });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = principal });
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = joinedSeq });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)leftSeq ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task AddMentionAsync(Guid gc, long seq, Guid target)
    {
        await using var cmd = db.DataSource.CreateCommand(
            "INSERT INTO gc_mention (gc_id, seq, target_principal_id) VALUES ($1, $2, $3)");
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gc });
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = seq });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = target });
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Owner_sees_live_gcs_with_events_written_by_others()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var gc = await db.CreateGcAsync();
        await ConsumerTestSupport.AppendAsync(db, gc, _other, 3, "o-owner", ct);
        await ConsumerTestSupport.AppendAsync(db, gc, _me, 2, "m-owner", ct);

        var items = await CreateReader().GetAsync(ConsumerTestSupport.Principal(_me, PrincipalKind.Owner), ct);

        var item = Assert.Single(items, i => i.GcId == gc);
        Assert.Equal(5, item.HeadSeq);
        Assert.Equal(0, item.LastReadSeq);
        Assert.Equal(3, item.UnreadEvents);
    }

    [Fact]
    public async Task Non_owner_sees_only_gcs_where_it_is_an_active_member()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var member = await db.CreateGcAsync();
        var stranger = await db.CreateGcAsync();
        await ConsumerTestSupport.AppendAsync(db, member, _other, 1, "member-gc", ct);
        await ConsumerTestSupport.AppendAsync(db, stranger, _other, 1, "stranger-gc", ct);
        await AddMemberAsync(member, _me);

        var items = await CreateReader().GetAsync(ConsumerTestSupport.Principal(_me), ct);

        Assert.Contains(items, i => i.GcId == member);
        Assert.DoesNotContain(items, i => i.GcId == stranger);
    }

    [Fact]
    public async Task A_member_who_left_gets_no_catch_up_for_that_gc()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var gc = await db.CreateGcAsync();
        await ConsumerTestSupport.AppendAsync(db, gc, _other, 2, "left-gc", ct);
        // The current row (highest joined_seq) carries left_seq, so the member has left.
        await AddMemberAsync(gc, _me, joinedSeq: 1, leftSeq: 1);

        var items = await CreateReader().GetAsync(ConsumerTestSupport.Principal(_me), ct);

        Assert.DoesNotContain(items, i => i.GcId == gc);
    }

    [Fact]
    public async Task Archived_gcs_are_excluded()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var gc = await db.CreateGcAsync(archived: true);
        await ConsumerTestSupport.AppendAsync(db, gc, _other, 2, "archived", ct);

        var items = await CreateReader().GetAsync(ConsumerTestSupport.Principal(_me, PrincipalKind.Owner), ct);

        Assert.DoesNotContain(items, i => i.GcId == gc);
    }

    [Fact]
    public async Task Unread_mentions_count_only_after_the_read_position()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var gc = await db.CreateGcAsync();
        await ConsumerTestSupport.AppendAsync(db, gc, _other, 4, "mentions", ct);
        await AddMemberAsync(gc, _me);
        await AddMentionAsync(gc, 1, _me);
        await AddMentionAsync(gc, 3, _me);
        await ConsumerTestSupport.SetReadStateAsync(db, _me, gc, 2);

        var item = Assert.Single(await CreateReader().GetAsync(ConsumerTestSupport.Principal(_me), ct), i => i.GcId == gc);

        Assert.Equal(2, item.UnreadEvents);
        Assert.Equal(1, item.UnreadMentions);
    }

    [Fact]
    public async Task A_gc_read_to_its_head_is_not_listed()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var gc = await db.CreateGcAsync();
        await ConsumerTestSupport.AppendAsync(db, gc, _other, 2, "read-to-head", ct);
        await AddMemberAsync(gc, _me);
        await ConsumerTestSupport.SetReadStateAsync(db, _me, gc, 2);

        var items = await CreateReader().GetAsync(ConsumerTestSupport.Principal(_me), ct);

        Assert.DoesNotContain(items, i => i.GcId == gc);
    }
}
