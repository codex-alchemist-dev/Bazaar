using Bazaar.RyuseiFuda.Orrery;
using Bazaar.Server.Security.Audit.Db;
using Bazaar.Server.Tests.Kanban.Data;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Tests.Ryusei;

/// <summary>
/// The committed path for the Observatory events and their audit rows (doc 38, Observability; DECISIONS-LOG round 19). A state
/// change writes its audit row in the same transaction, and its event goes out only after the commit. Uses the Kanban fixture.
/// This container has no Docker, so these tests are not run here; CI is the verifier.
/// </summary>
public sealed class RyuseiObservatoryDatabaseTests(KanbanDb db) : IClassFixture<KanbanDb>
{
    private static readonly DateTimeOffset _base = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
    private const string _purpose = "watch the board for the release tag";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private static RyuseiActor Owner { get; } = RyuseiActor.ForOwner(Guid.Parse("aaaaaaaa-aaaa-7aaa-8aaa-aaaaaaaaaaaa"));

    private async Task<RyuseiTriggerRecord> InsertActiveTimerAsync(Guid projectId)
    {
        var spec = new RyuseiTimerTriggerSpec(_base.AddHours(1));
        var trigger = new NewRyuseiTrigger(
            Guid.CreateVersion7(),
            projectId,
            Guid.CreateVersion7(),
            RyuseiTriggerKind.Timer,
            [Guid.CreateVersion7()],
            _purpose,
            RyuseiSpecJson.Encode(spec),
            TaskId: null,
            AttachGcId: null,
            RyuseiPingMode.None,
            spec.FireAt,
            MaxFires: null,
            ExpiresAt: null,
            ChainDepth: 0);

        await using var conn = await db.DataSource.OpenConnectionAsync(Ct);
        await using var tx = await conn.BeginTransactionAsync(Ct);
        var row = await new RyuseiTriggerRepository(db.DataSource).InsertAsync(tx, trigger, Ct);
        await tx.CommitAsync(Ct);
        return row;
    }

    private async Task<List<string>> AuditKindsForAsync(Guid triggerId)
    {
        await using var cmd = db.DataSource.CreateCommand("SELECT kind FROM audit_log WHERE payload->>'triggerId' = @id ORDER BY seq");
        cmd.Parameters.AddWithValue("id", triggerId.ToString("D"));
        var kinds = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            kinds.Add(reader.GetString(0));
        }

        return kinds;
    }

    [Fact]
    public async Task Pause_writes_one_audit_row_and_emits_one_event_after_commit()
    {
        SkipWithoutDatabase();
        var projectId = Guid.CreateVersion7();
        var row = await InsertActiveTimerAsync(projectId);
        var sink = new RecordingSink();
        var service = new RyuseiTriggerService(db.DataSource, new PostgresAuditLog(db.DataSource), observatory: sink);

        var result = await service.PauseAsync(Owner, row.TriggerId, _base, Ct);

        Assert.True(result.IsOk);
        Assert.Equal(["ryusei.trigger.paused"], await AuditKindsForAsync(row.TriggerId));
        var evt = Assert.Single(sink.Events);
        Assert.Equal("ryusei.trigger_paused", evt.Name);
        Assert.Equal(row.TriggerId.ToString("D"), evt.Fields["triggerId"]!.GetValue<string>());
        Assert.Equal("active", evt.Fields["fromState"]!.GetValue<string>());
        Assert.Equal("paused", evt.Fields["toState"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_refused_pause_writes_no_state_event()
    {
        SkipWithoutDatabase();
        var projectId = Guid.CreateVersion7();
        var row = await InsertActiveTimerAsync(projectId);
        var sink = new RecordingSink();
        var service = new RyuseiTriggerService(db.DataSource, new PostgresAuditLog(db.DataSource), observatory: sink);

        // A stranger agent that is not a target cannot pause the trigger.
        var stranger = RyuseiActor.ForAgent(Guid.CreateVersion7(), projectId);
        var result = await service.PauseAsync(stranger, row.TriggerId, _base, Ct);

        Assert.False(result.IsOk);
        Assert.Empty(sink.Events);
    }

    [Fact]
    public async Task Create_emits_trigger_created_with_a_purpose_hash_and_no_purpose_text()
    {
        SkipWithoutDatabase();
        var projectId = Guid.CreateVersion7();
        var target = Guid.CreateVersion7();
        var sink = new RecordingSink();
        var service = new RyuseiTriggerService(db.DataSource, new PostgresAuditLog(db.DataSource), observatory: sink);

        var result = await service.CreateAsync(
            Owner,
            projectId,
            [new RyuseiTargetInfo(target, projectId, IsActive: true)],
            RyuseiTriggerKind.Timer,
            new RyuseiTimerTriggerSpec(_base.AddHours(2)),
            _purpose,
            taskId: null,
            attachGcId: null,
            RyuseiPingMode.None,
            chainDepth: 0,
            _base,
            ct: Ct);

        Assert.True(result.IsOk);
        var evt = Assert.Single(sink.Events);
        Assert.Equal("ryusei.trigger_created", evt.Name);
        Assert.Equal(RyuseiObservatoryEvents.Hash(_purpose), evt.Fields["purposeHash"]!.GetValue<string>());
        Assert.DoesNotContain(_purpose, evt.Fields.ToJsonString());
    }

    private sealed class RecordingSink : IRyuseiObservatory
    {
        public List<RyuseiObservatoryEvent> Events { get; } = [];

        public ValueTask EmitAsync(RyuseiObservatoryEvent evt, CancellationToken ct = default)
        {
            Events.Add(evt);
            return ValueTask.CompletedTask;
        }
    }
}
