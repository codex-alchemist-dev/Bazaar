using Bazaar.RyuseiFuda.Orrery;
using Bazaar.Server.Tests.Kanban.Data;
using Bazaar.Testing.Postgres;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Bazaar.Server.Tests.Ryusei;

/// <summary>
/// The RyuseiFuda guard trips on real PostgreSQL (doc 38, WP-R8): a trip pauses the trigger, writes one <c>guard_event</c> row in the
/// same transaction, calls the Psych Ward hook once after commit, and a second pass does not repeat it. Two concurrent ticks fire a
/// timer once (single flight). Uses the Kanban fixture, which migrates every namespace and the ryusei <c>guard_event</c> table.
/// This container has no Docker, so these tests are not run here; CI is the verifier.
/// </summary>
public sealed class RyuseiGuardTripSqlTests(KanbanDb db) : IClassFixture<KanbanDb>
{
    private static readonly DateTimeOffset _base = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] _refusedLoop = ["refused_loop"];
    private static readonly string[] _woke = ["woke"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private RyuseiTriggerRepository Repo => new(db.DataSource);

    private static FakeTimeProvider Clock(DateTimeOffset now) => new(now);

    private RyuseiTickService Tick(TimeProvider time, RyuseiLimits limits, IPsychWardAdmission ward) =>
        new(db.DataSource, limits, time, ward: ward);

    private async Task InsertAgentAsync(Guid agentId, string state)
    {
        await using var cmd = db.DataSource.CreateCommand("INSERT INTO agent (agent_id, name, state) VALUES (@id, 'guard test', @state)");
        cmd.Parameters.AddWithValue("id", agentId);
        cmd.Parameters.AddWithValue("state", state);
        await cmd.ExecuteNonQueryAsync(Ct);
    }

    private async Task<RyuseiTriggerRecord> InsertTimerAsync(DateTimeOffset fireAt, int chainDepth, Guid agentId)
    {
        var spec = new RyuseiTimerTriggerSpec(fireAt);
        var trigger = new NewRyuseiTrigger(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            RyuseiTriggerKind.Timer,
            [agentId],
            "check the build",
            RyuseiSpecJson.Encode(spec),
            TaskId: null,
            AttachGcId: null,
            RyuseiPingMode.None,
            fireAt,
            MaxFires: null,
            ExpiresAt: null,
            ChainDepth: chainDepth);

        await using var conn = await db.DataSource.OpenConnectionAsync(Ct);
        await using var tx = await conn.BeginTransactionAsync(Ct);
        var row = await Repo.InsertAsync(tx, trigger, Ct);
        await tx.CommitAsync(Ct);
        return row;
    }

    private async Task<long> GuardEventCountAsync(string kind, Guid triggerId)
    {
        await using var cmd = db.DataSource.CreateCommand(
            "SELECT count(*) FROM guard_event WHERE kind = @kind AND counters->>'triggerId' = @trigger_id");
        cmd.Parameters.AddWithValue("kind", kind);
        cmd.Parameters.AddWithValue("trigger_id", triggerId.ToString("D"));
        return (long)(await cmd.ExecuteScalarAsync(Ct))!;
    }

    private async Task<string[]> FireOutcomesAsync(Guid triggerId)
    {
        await using var cmd = db.DataSource.CreateCommand("SELECT outcome FROM ryusei_fire WHERE trigger_id = @trigger_id ORDER BY scheduled_for");
        cmd.Parameters.AddWithValue("trigger_id", triggerId);
        var rows = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(reader.GetString(0));
        }

        return rows.ToArray();
    }

    private async Task<int> InboxCountAsync(Guid agentId)
    {
        await using var cmd = db.DataSource.CreateCommand("SELECT count(*) FROM inbox_item WHERE agent_id = @agent_id");
        cmd.Parameters.AddWithValue("agent_id", agentId);
        return checked((int)(long)(await cmd.ExecuteScalarAsync(Ct))!);
    }

    [Fact]
    public async Task A_chain_depth_trip_pauses_the_trigger_records_guard_event_and_calls_the_ward_once()
    {
        SkipWithoutDatabase();
        var agent = Guid.CreateVersion7();
        await InsertAgentAsync(agent, "active");
        var fireAt = _base.AddHours(1);
        var row = await InsertTimerAsync(fireAt, chainDepth: 3, agent);
        var ward = new RecordingWard();
        var limits = new RyuseiLimits { MaxChainDepth = 2 };

        await Tick(Clock(fireAt.AddMinutes(1)), limits, ward).TickAsync(Ct);

        var stored = await Repo.GetAsync(row.TriggerId, Ct);
        Assert.NotNull(stored);
        Assert.Equal(RyuseiTriggerState.Paused, stored.State);
        Assert.Equal(_refusedLoop, await FireOutcomesAsync(row.TriggerId));
        Assert.Equal(1L, await GuardEventCountAsync("ryusei_chain_depth", row.TriggerId));
        Assert.Equal(0, await InboxCountAsync(agent));

        var admission = Assert.Single(ward.Admissions);
        Assert.Equal(row.TriggerId, admission.TriggerId);
        Assert.Equal(RyuseiRefusal.ChainDepthExceeded, admission.Trip);
        Assert.Equal("ryusei_chain_depth", admission.GuardKind);
        Assert.Equal(new[] { agent }, admission.AgentIds);
    }

    [Fact]
    public async Task A_second_pass_does_not_repeat_a_trip_or_the_ward_call()
    {
        SkipWithoutDatabase();
        var agent = Guid.CreateVersion7();
        await InsertAgentAsync(agent, "active");
        var fireAt = _base.AddHours(1);
        var row = await InsertTimerAsync(fireAt, chainDepth: 3, agent);
        var ward = new RecordingWard();
        var limits = new RyuseiLimits { MaxChainDepth = 2 };
        var time = Clock(fireAt.AddMinutes(1));

        await Tick(time, limits, ward).TickAsync(Ct);
        time.Advance(TimeSpan.FromMinutes(5));
        await Tick(time, limits, ward).TickAsync(Ct);

        Assert.Single(await FireOutcomesAsync(row.TriggerId));
        Assert.Equal(1L, await GuardEventCountAsync("ryusei_chain_depth", row.TriggerId));
        Assert.Single(ward.Admissions);
    }

    [Fact]
    public async Task Two_concurrent_ticks_fire_a_due_timer_once()
    {
        SkipWithoutDatabase();
        var agent = Guid.CreateVersion7();
        await InsertAgentAsync(agent, "active");
        var fireAt = _base.AddHours(1);
        var row = await InsertTimerAsync(fireAt, chainDepth: 0, agent);
        var time = Clock(fireAt.AddMinutes(1));
        var tick = Tick(time, new RyuseiLimits(), new RecordingWard());

        await Task.WhenAll(tick.TickAsync(Ct), tick.TickAsync(Ct));

        Assert.Equal(_woke, await FireOutcomesAsync(row.TriggerId));
        Assert.Equal(1, await InboxCountAsync(agent));
    }

    [Fact]
    public async Task A_guard_event_row_cannot_be_changed_or_removed()
    {
        SkipWithoutDatabase();
        var agent = Guid.CreateVersion7();
        await InsertAgentAsync(agent, "active");
        var fireAt = _base.AddHours(1);
        var row = await InsertTimerAsync(fireAt, chainDepth: 3, agent);
        await Tick(Clock(fireAt.AddMinutes(1)), new RyuseiLimits { MaxChainDepth = 2 }, new RecordingWard()).TickAsync(Ct);

        await using var update = db.DataSource.CreateCommand("UPDATE guard_event SET kind = 'x' WHERE counters->>'triggerId' = @trigger_id");
        update.Parameters.AddWithValue("trigger_id", row.TriggerId.ToString("D"));
        var ex = await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync(Ct));
        Assert.Equal("23001", ex.SqlState);
    }

    /// <summary>Records each admission. A real Psych Ward is not built yet (doc 26), so the test stands in for it.</summary>
    private sealed class RecordingWard : IPsychWardAdmission
    {
        public List<PsychWardAdmission> Admissions { get; } = [];

        public Task AdmitAsync(PsychWardAdmission admission, CancellationToken ct)
        {
            Admissions.Add(admission);
            return Task.CompletedTask;
        }
    }
}
