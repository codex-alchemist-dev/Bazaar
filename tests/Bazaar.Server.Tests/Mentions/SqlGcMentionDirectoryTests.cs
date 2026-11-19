using Bazaar.Contracts.Gc;
using Bazaar.Server.Agents.Turns.Posting;
using Bazaar.Server.Mentions;
using Bazaar.Server.Tests.Gc;
using Npgsql;

namespace Bazaar.Server.Tests.Mentions;

/// <summary><see cref="SqlGcMentionDirectory"/> against the real schema: current members joined to their handles.</summary>
public sealed class SqlGcMentionDirectoryTests(GcDb db) : IClassFixture<GcDb>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private SqlGcMentionDirectory Directory => new(db.DataSource);

    private PrincipalHandleRepository Handles => new(db.DataSource);

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..24];

    private async Task AddMemberAsync(Guid gc, Guid principal, long joinedSeq, long? leftSeq = null, string role = "member")
    {
        await using var cmd = db.DataSource.CreateCommand(
            "INSERT INTO gc_member (gc_id, principal_id, role, joined_seq, left_seq) VALUES ($1, $2, $3, $4, $5)");
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gc });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = principal });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = role });
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = joinedSeq });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)leftSeq ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint });
        await cmd.ExecuteNonQueryAsync(Ct);
    }

    private async Task<Guid> MemberWithHandleAsync(Guid gc, string handle, PrincipalKind kind = PrincipalKind.Agent)
    {
        var principal = Guid.CreateVersion7();
        await AddMemberAsync(gc, principal, 1);
        await Handles.SetAsync(principal, kind, handle, Ct);
        return principal;
    }

    /// <summary>The instance's owner id, creating the owner row when the class database has none yet.</summary>
    private async Task<Guid> OwnerAsync()
    {
        await using (var cmd = db.DataSource.CreateCommand(
            "INSERT INTO human (id, display_name, role, status) VALUES ($1, 'Owner', 'owner', 'active') ON CONFLICT DO NOTHING"))
        {
            cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = Guid.CreateVersion7() });
            await cmd.ExecuteNonQueryAsync(Ct);
        }

        return await db.ScalarAsync<Guid>("SELECT id FROM human WHERE role = 'owner'");
    }

    [Fact]
    public async Task Members_with_handles_are_in_the_roster()
    {
        GcTestSkip.WithoutDatabase();
        var gc = await db.CreateGcAsync();
        var anaHandle = Unique("ana");
        var benHandle = Unique("ben");
        var ana = await MemberWithHandleAsync(gc, anaHandle);
        var ben = await MemberWithHandleAsync(gc, benHandle, PrincipalKind.Human);

        var roster = await Directory.LoadAsync(gc, Ct);

        Assert.Equal(
            new[] { new GcMentionMember(ana, anaHandle), new GcMentionMember(ben, benHandle) }.OrderBy(m => m.Handle, StringComparer.Ordinal),
            roster.ActiveMembers.OrderBy(m => m.Handle, StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_member_without_a_handle_is_not_in_the_roster()
    {
        GcTestSkip.WithoutDatabase();
        var gc = await db.CreateGcAsync();
        var withHandle = await MemberWithHandleAsync(gc, Unique("named"));
        await AddMemberAsync(gc, Guid.CreateVersion7(), 1);

        var roster = await Directory.LoadAsync(gc, Ct);

        Assert.Equal(withHandle, Assert.Single(roster.ActiveMembers).PrincipalId);
    }

    [Fact]
    public async Task A_principal_with_a_handle_that_is_not_a_member_is_not_in_the_roster()
    {
        GcTestSkip.WithoutDatabase();
        var gc = await db.CreateGcAsync();
        var otherGc = await db.CreateGcAsync();
        var member = await MemberWithHandleAsync(gc, Unique("inside"));
        await MemberWithHandleAsync(otherGc, Unique("elsewhere"));
        await Handles.SetAsync(Guid.CreateVersion7(), PrincipalKind.Agent, Unique("nowhere"), Ct);

        var roster = await Directory.LoadAsync(gc, Ct);

        Assert.Equal(member, Assert.Single(roster.ActiveMembers).PrincipalId);
    }

    [Fact]
    public async Task A_member_who_left_is_not_in_the_roster()
    {
        GcTestSkip.WithoutDatabase();
        var gc = await db.CreateGcAsync();
        var stays = await MemberWithHandleAsync(gc, Unique("stays"));
        var left = await MemberWithHandleAsync(gc, Unique("left"));
        // gc_member is append-only: leaving is a newer row with left_seq set.
        await AddMemberAsync(gc, left, 5, leftSeq: 9);

        var roster = await Directory.LoadAsync(gc, Ct);

        Assert.Equal(stays, Assert.Single(roster.ActiveMembers).PrincipalId);
    }

    [Fact]
    public async Task A_member_who_rejoined_is_in_the_roster_once()
    {
        GcTestSkip.WithoutDatabase();
        var gc = await db.CreateGcAsync();
        var back = await MemberWithHandleAsync(gc, Unique("back"));
        await AddMemberAsync(gc, back, 5, leftSeq: 9);
        await AddMemberAsync(gc, back, 12);

        var roster = await Directory.LoadAsync(gc, Ct);

        Assert.Equal(back, Assert.Single(roster.ActiveMembers).PrincipalId);
    }

    [Fact]
    public async Task An_observer_with_a_handle_is_in_the_roster()
    {
        GcTestSkip.WithoutDatabase();
        var gc = await db.CreateGcAsync();
        var observer = Guid.CreateVersion7();
        await AddMemberAsync(gc, observer, 1, role: "observer");
        await Handles.SetAsync(observer, PrincipalKind.Human, Unique("watcher"), Ct);

        var roster = await Directory.LoadAsync(gc, Ct);

        Assert.Equal(observer, Assert.Single(roster.ActiveMembers).PrincipalId);
    }

    [Fact]
    public async Task A_changed_handle_shows_in_the_next_load()
    {
        GcTestSkip.WithoutDatabase();
        var gc = await db.CreateGcAsync();
        var member = await MemberWithHandleAsync(gc, Unique("before"));
        var after = Unique("after");
        await Handles.SetAsync(member, PrincipalKind.Agent, after, Ct);

        var roster = await Directory.LoadAsync(gc, Ct);

        Assert.Equal(new GcMentionMember(member, after), Assert.Single(roster.ActiveMembers));
    }

    [Fact]
    public async Task An_unknown_gc_has_an_empty_roster()
    {
        GcTestSkip.WithoutDatabase();

        var roster = await Directory.LoadAsync(Guid.CreateVersion7(), Ct);

        Assert.Empty(roster.ActiveMembers);
    }

    [Fact]
    public async Task The_roster_names_the_owner()
    {
        GcTestSkip.WithoutDatabase();
        var owner = await OwnerAsync();
        var gc = await db.CreateGcAsync();

        var roster = await Directory.LoadAsync(gc, Ct);

        Assert.Equal(owner, roster.OwnerId);
        // The owner is not a member of this GC and has no handle, so it is not parsed from a post.
        Assert.Empty(roster.ActiveMembers);
    }

    [Fact]
    public async Task The_roster_feeds_the_mention_parser()
    {
        GcTestSkip.WithoutDatabase();
        var gc = await db.CreateGcAsync();
        var handle = Unique("target");
        var target = await MemberWithHandleAsync(gc, handle);
        await AddMemberAsync(gc, Guid.CreateVersion7(), 1);

        var roster = await Directory.LoadAsync(gc, Ct);
        var scan = AgentMentionParser.Scan($"over to @{handle}.", roster.ActiveMembers);

        Assert.Equal(target, Assert.Single(scan.Spans).PrincipalId);
    }
}
