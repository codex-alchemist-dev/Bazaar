using Bazaar.Server.Gc.Members;
using Npgsql;

namespace Bazaar.Server.Tests.Gc.Wire;

/// <summary>The Postgres <c>gc.list</c> membership source over the real schema.</summary>
public sealed class GcMemberListSourceTests(GcDb db) : IClassFixture<GcDb>
{
    // The Postgres source: membership rows decide, nothing else.

    private async Task<Guid> NewGcAsync(string type, string title, bool archived = false)
    {
        var id = Guid.CreateVersion7();
        await using var cmd = db.DataSource.CreateCommand(
            "INSERT INTO gc (gc_id, type_key, title, created_by, archived_at) VALUES ($1, $2, $3, $1, CASE WHEN $4 THEN now() END)");
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = type });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = title });
        cmd.Parameters.Add(new NpgsqlParameter<bool> { TypedValue = archived });
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return id;
    }

    private async Task JoinAsync(Guid gc, Guid principal, long? leftSeq = null)
    {
        await using var cmd = db.DataSource.CreateCommand(
            "INSERT INTO gc_member (gc_id, principal_id, role, joined_seq, left_seq) VALUES ($1, $2, 'member', 0, $3)");
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gc });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = principal });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)leftSeq ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task The_postgres_source_lists_only_active_memberships_and_hides_archived()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var me = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();
        var mine = await NewGcAsync("general", "mine");
        await JoinAsync(mine, me);
        await JoinAsync(mine, other);
        var dm = await NewGcAsync("dm", "dm with me");
        await JoinAsync(dm, me);
        var left = await NewGcAsync("general", "left");
        await JoinAsync(left, me, leftSeq: 3);
        var archived = await NewGcAsync("general", "archived", archived: true);
        await JoinAsync(archived, me);
        var notMine = await NewGcAsync("general", "not mine");
        await JoinAsync(notMine, other);
        var dmOfOthers = await NewGcAsync("dm", "others dm");
        await JoinAsync(dmOfOthers, other);

        var source = new PgGcMemberListSource(db.DataSource);
        var rows = await source.ListAsync(me, includeArchived: false, ct);

        Assert.Equal(new HashSet<Guid> { mine, dm }, rows.Select(r => r.GcId).ToHashSet());
        Assert.Equal(2, rows.Single(r => r.GcId == mine).MemberCount);
        Assert.Equal("mine", rows.Single(r => r.GcId == mine).Title);
        Assert.Equal("dm", rows.Single(r => r.GcId == dm).TypeKey);

        var withArchived = await source.ListAsync(me, includeArchived: true, ct);
        Assert.Contains(withArchived, r => r.GcId == archived && r.Archived);

        Assert.Empty(await source.ListAsync(Guid.CreateVersion7(), includeArchived: true, ct));
    }
}
