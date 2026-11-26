using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc;
using Bazaar.Server.Gc.Read;
using Bazaar.Server.Gc.Write;
using Npgsql;

namespace Bazaar.Server.Tests.Gc.Read;

/// <summary>Postgres-backed viewer rules (<see cref="GcReadAccess"/>): membership, left_seq, DM approval, Thinking GCs.</summary>
public sealed class GcReadAccessTests(GcDb db) : IClassFixture<GcDb>
{
    private static readonly Guid _owner = Guid.Parse("00000000-0000-7000-8000-0000000000aa");
    private static readonly Guid _humanA = Guid.CreateVersion7();
    private static readonly Guid _humanB = Guid.CreateVersion7();
    private static readonly Guid _agentA = Guid.CreateVersion7();
    private static readonly Guid _agentB = Guid.CreateVersion7();

    private static GcPrincipal Owner => new(_owner, PrincipalKind.Owner, "owner");

    private static GcPrincipal Human(Guid id) => new(id, PrincipalKind.Human, "human");

    private static GcPrincipal Agent(Guid id) => new(id, PrincipalKind.Agent, "agent");

    private GcReadAccess Access => new(db.DataSource);

    private async Task<Guid> CreateGcAsync(string typeKey, bool archived = false)
    {
        var id = Guid.CreateVersion7();
        await using var cmd = db.DataSource.CreateCommand(
            "INSERT INTO gc (gc_id, type_key, title, created_by, archived_at) VALUES ($1, $2, 'test', $3, CASE WHEN $4 THEN now() END)");
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = typeKey });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = _owner });
        cmd.Parameters.Add(new NpgsqlParameter<bool> { TypedValue = archived });
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    private async Task AddMemberAsync(Guid gc, Guid principal, string role, long joinedSeq, long? leftSeq = null)
    {
        await using var cmd = db.DataSource.CreateCommand(
            "INSERT INTO gc_member (gc_id, principal_id, role, joined_seq, left_seq) VALUES ($1, $2, $3, $4, $5)");
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gc });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = principal });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = role });
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = joinedSeq });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)leftSeq ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task RequestAccessAsync(Guid gc, Guid requester, bool approved)
    {
        await using var cmd = db.DataSource.CreateCommand(
            """
            INSERT INTO gc_access_request (gc_id, requester, status, decided_by, decided_at)
            VALUES ($1, $2, $3, $4, CASE WHEN $5 THEN now() END)
            """);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gc });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = requester });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = approved ? "approved" : "pending" });
        cmd.Parameters.Add(new NpgsqlParameter { Value = approved ? _owner : DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter<bool> { TypedValue = approved });
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<List<long>> ReadSeqsAsync(Guid gc, GcPrincipal viewer, CancellationToken ct)
    {
        var reader = new GcLogReader(db.DataSource, Access);
        var seqs = new List<long>();
        await foreach (var e in reader.ReadAsync(gc, afterSeq: 0, limit: IGcLogReader.MaxLimit, viewer, ct))
            seqs.Add(e.Seq);
        return seqs;
    }

    // ---------------------------------------------------------------- Grants

    [Fact]
    public async Task Unknown_gc_is_denied()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;

        var grant = await Access.GetGrantAsync(Guid.CreateVersion7(), Owner, ct);

        Assert.Equal(GcReadGrant.Denied, grant);
    }

    [Fact]
    public async Task Owner_reads_every_gc_type_without_a_row()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var general = await CreateGcAsync("general");
        var dm = await CreateGcAsync("dm");
        var thinking = await CreateGcAsync("thinking");

        Assert.Equal(GcReadGrant.Full, await Access.GetGrantAsync(general, Owner, ct));
        Assert.Equal(GcReadGrant.Full, await Access.GetGrantAsync(dm, Owner, ct));
        Assert.Equal(GcReadGrant.Full, await Access.GetGrantAsync(thinking, Owner, ct));
    }

    [Fact]
    public async Task Active_member_and_observer_read_the_whole_log()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var gc = await CreateGcAsync("general");
        await AddMemberAsync(gc, _humanA, "member", 0);
        await AddMemberAsync(gc, _humanB, "observer", 0);

        Assert.Equal(GcReadGrant.Full, await Access.GetGrantAsync(gc, Human(_humanA), ct));
        Assert.Equal(GcReadGrant.Full, await Access.GetGrantAsync(gc, Human(_humanB), ct));
    }

    [Fact]
    public async Task Non_member_is_denied_on_a_general_gc()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var gc = await CreateGcAsync("general");
        await AddMemberAsync(gc, _humanA, "member", 0);

        Assert.Equal(GcReadGrant.Denied, await Access.GetGrantAsync(gc, Human(_humanB), ct));
    }

    [Fact]
    public async Task Former_member_is_limited_to_the_leave_seq()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var gc = await CreateGcAsync("general");
        await AddMemberAsync(gc, _humanA, "member", 0, leftSeq: 3);

        Assert.Equal(GcReadGrant.Through(3), await Access.GetGrantAsync(gc, Human(_humanA), ct));
    }

    [Fact]
    public async Task Rejoined_member_gets_full_read_because_the_highest_joined_seq_is_current()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var gc = await CreateGcAsync("general");
        await AddMemberAsync(gc, _humanA, "member", 0, leftSeq: 2);
        await AddMemberAsync(gc, _humanA, "member", 4);

        Assert.Equal(GcReadGrant.Full, await Access.GetGrantAsync(gc, Human(_humanA), ct));
    }

    [Fact]
    public async Task Archived_gc_stays_readable_by_members()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var gc = await CreateGcAsync("general", archived: true);
        await AddMemberAsync(gc, _humanA, "member", 0);

        Assert.Equal(GcReadGrant.Full, await Access.GetGrantAsync(gc, Human(_humanA), ct));
    }

    [Fact]
    public async Task Dm_is_denied_to_non_members_until_an_access_request_is_approved()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var dm = await CreateGcAsync("dm");
        await AddMemberAsync(dm, _humanA, "member", 0);
        await AddMemberAsync(dm, _humanB, "member", 0);
        var outsider = Human(Guid.CreateVersion7());
        await RequestAccessAsync(dm, outsider.Id, approved: false);

        Assert.Equal(GcReadGrant.Denied, await Access.GetGrantAsync(dm, outsider, ct));

        await RequestAccessAsync(dm, outsider.Id, approved: true);

        Assert.Equal(GcReadGrant.Full, await Access.GetGrantAsync(dm, outsider, ct));
        Assert.Equal(GcReadGrant.Denied, await Access.GetGrantAsync(dm, Human(Guid.CreateVersion7()), ct));
    }

    [Fact]
    public async Task Thinking_gc_is_readable_by_its_owning_agent_and_denied_to_other_agents()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var thinking = await CreateGcAsync("thinking");
        await AddMemberAsync(thinking, _agentA, "member", 0);

        Assert.Equal(GcReadGrant.Full, await Access.GetGrantAsync(thinking, Agent(_agentA), ct));
        Assert.Equal(GcReadGrant.Denied, await Access.GetGrantAsync(thinking, Agent(_agentB), ct));
        Assert.Equal(GcReadGrant.Denied, await Access.GetGrantAsync(thinking, Human(_humanA), ct));
    }

    [Fact]
    public async Task Thinking_gc_is_readable_by_a_peer_agent_once_approved()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var thinking = await CreateGcAsync("thinking");
        await AddMemberAsync(thinking, _agentA, "member", 0);
        await RequestAccessAsync(thinking, _agentB, approved: true);

        Assert.Equal(GcReadGrant.Full, await Access.GetGrantAsync(thinking, Agent(_agentB), ct));
    }

    // ---------------------------------------------------------------- Through the reader

    [Fact]
    public async Task Reader_stops_a_former_member_at_their_leave_seq()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var gc = await CreateGcAsync("general");
        await AddMemberAsync(gc, _humanA, "member", 0);
        await AddMemberAsync(gc, _humanB, "member", 0);
        var writer = new GcLogWriter(db.DataSource, new RecordingNotifier());
        await writer.AppendManyAsync(Enumerable.Range(0, 6).Select(i => GcTestData.System(gc, $"e{i}")).ToList(), ct);
        // Human A leaves at seq 3: the removal is recorded as a new row whose LeftSeq is its own seq.
        await AddMemberAsync(gc, _humanA, "member", joinedSeq: 3, leftSeq: 3);

        var formerSeqs = await ReadSeqsAsync(gc, Human(_humanA), ct);
        var stillInSeqs = await ReadSeqsAsync(gc, Human(_humanB), ct);

        Assert.Equal([1L, 2L, 3L], formerSeqs);
        Assert.Equal([1L, 2L, 3L, 4L, 5L, 6L], stillInSeqs);
    }
}
