using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc;
using Bazaar.Server.Gc.Read;
using Bazaar.Server.Gc.Write;
using Bazaar.Server.GcMetadata;
using Bazaar.Server.Security.Audit;
using Npgsql;

namespace Bazaar.Server.Tests.Gc.Read;

/// <summary>
/// The DM access flow on real Postgres: request, owner grant, and a read that starts at the grant's system event
/// (docs/design/32, Per-GC; docs/design/01, DM owner-read flow).
/// </summary>
public sealed class DmAccessPostgresTests(GcDb db) : IClassFixture<GcDb>
{
    private static readonly Guid _owner = Guid.Parse("00000000-0000-7000-8000-0000000000aa");

    private sealed class MemoryAudit : IAuditLog
    {
        public readonly List<AuditEntry> Entries = [];

        public Task AppendAsync(AuditEntry e, CancellationToken ct = default)
        {
            Entries.Add(e);
            return Task.CompletedTask;
        }
    }

    private async Task<Guid> CreateDmAsync(params Guid[] members)
    {
        var id = Guid.CreateVersion7();
        await using (var cmd = db.DataSource.CreateCommand("INSERT INTO gc (gc_id, type_key, title, created_by) VALUES ($1, 'dm', 'dm', $2)"))
        {
            cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
            cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = _owner });
            await cmd.ExecuteNonQueryAsync();
        }

        foreach (var member in members)
        {
            await using var cmd = db.DataSource.CreateCommand("INSERT INTO gc_member (gc_id, principal_id, role, joined_seq) VALUES ($1, $2, 'member', 0)");
            cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
            cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = member });
            await cmd.ExecuteNonQueryAsync();
        }

        return id;
    }

    [Fact]
    public async Task Request_then_owner_grant_lets_the_requester_read_from_the_grant_event_onward()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var alice = Guid.CreateVersion7();
        var bob = Guid.CreateVersion7();
        var carol = new DmActor(Guid.CreateVersion7(), PrincipalKind.Human);
        var owner = new DmActor(_owner, PrincipalKind.Owner);
        var dm = await CreateDmAsync(alice, bob);

        var writer = new GcLogWriter(db.DataSource, new RecordingNotifier());
        await writer.AppendManyAsync(Enumerable.Range(0, 3).Select(i => GcTestData.System(dm, $"before{i}")).ToList(), ct);

        var audit = new MemoryAudit();
        var service = new DmAccessService(new PgDmDirectory(db.DataSource), new PgDmAccessLedger(db.DataSource), new GcDmAccessEvents(writer), audit);
        var access = new GcReadAccess(db.DataSource);
        var reader = new GcLogReader(db.DataSource, access);
        var carolPrincipal = new GcPrincipal(carol.Id, PrincipalKind.Human, "human");

        var request = await service.RequestAsync(carol, dm, ct);
        Assert.Equal(DmAccessOutcome.Requested, request.Outcome);
        Assert.False((await service.AuthorizeReadAsync(carol, dm, ct)).Allowed);

        var decision = await service.DecideAsync(owner, request.Request!.Id, approve: true, ct);
        Assert.Equal(DmAccessOutcome.Granted, decision.Outcome);
        var grantSeq = decision.Request!.DecidedSeq!.Value;

        await writer.AppendManyAsync([GcTestData.System(dm, "after")], ct);

        var seqs = new List<long>();
        await foreach (var e in reader.ReadAsync(dm, 0, IGcLogReader.MaxLimit, carolPrincipal, ct))
            seqs.Add(e.Seq);

        Assert.NotEmpty(seqs);
        Assert.Equal(grantSeq, seqs[0]);
        Assert.DoesNotContain(seqs, s => s < grantSeq);
        Assert.Contains(audit.Entries, e => e.Kind == "dm.access.granted");
        Assert.True((await service.AuthorizeReadAsync(carol, dm, ct)).Allowed);
    }

    [Fact]
    public async Task Directory_finds_only_dms_and_lists_active_members()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var alice = Guid.CreateVersion7();
        var dm = await CreateDmAsync(alice);
        var general = await db.CreateGcAsync();
        var directory = new PgDmDirectory(db.DataSource);

        var found = await directory.FindAsync(dm, ct);

        Assert.Equal([alice], found!.ActiveMembers);
        Assert.Null(await directory.FindAsync(general, ct));
        Assert.Null(await directory.FindAsync(Guid.NewGuid(), ct));
    }

    [Fact]
    public async Task Ledger_resolves_once_and_keeps_the_first_decided_seq()
    {
        GcTestSkip.WithoutDatabase();
        var ct = TestContext.Current.CancellationToken;
        var dm = await CreateDmAsync(Guid.CreateVersion7());
        var ledger = new PgDmAccessLedger(db.DataSource);
        var requester = Guid.CreateVersion7();
        var row = await ledger.CreateAsync(dm, requester, DateTimeOffset.UtcNow, ct);

        Assert.True(await ledger.ResolveAsync(row.Id, DmAccessStatus.Approved, _owner, DateTimeOffset.UtcNow, ct));
        Assert.False(await ledger.ResolveAsync(row.Id, DmAccessStatus.Denied, _owner, DateTimeOffset.UtcNow, ct));
        await ledger.SetDecidedSeqAsync(row.Id, 5, ct);
        await ledger.SetDecidedSeqAsync(row.Id, 9, ct);

        var read = await ledger.GetAsync(row.Id, ct);
        Assert.Equal(DmAccessStatus.Approved, read!.Status);
        Assert.Equal(5L, read.DecidedSeq);
    }
}
