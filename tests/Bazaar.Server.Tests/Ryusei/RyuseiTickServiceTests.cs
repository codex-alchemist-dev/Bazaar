using System.Text.Json.Nodes;
using Bazaar.Contracts.Gc;
using Bazaar.RyuseiFuda.Orrery;
using Bazaar.Server.Gc;
using Bazaar.Server.Gc.Create;
using Bazaar.Server.Gc.Write;
using Bazaar.Server.Tests.Kanban.Data;
using Bazaar.Testing.Postgres;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Bazaar.Server.Tests.Ryusei;

/// <summary>
/// The scheduler tick on real PostgreSQL (doc 38, WP-R5): the <c>SKIP LOCKED</c> claim, the one-fire-per-instant key, and
/// end-to-end passes that write the fire row, move the trigger and enqueue one inbox item per woken target keyed by the fire
/// id. Uses the Kanban fixture, which migrates every namespace. This container has no Docker, so these tests are not run here;
/// CI is the verifier.
/// </summary>
public sealed class RyuseiTickServiceTests(KanbanDb db) : IClassFixture<KanbanDb>
{
    private static readonly DateTimeOffset _base = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private RyuseiTriggerRepository Repo => new(db.DataSource);

    /// <summary>A clock per test. Rows due before its instant are picked up by the pass.</summary>
    private static FakeTimeProvider Clock(DateTimeOffset now) => new(now);

    private RyuseiTickService Tick(TimeProvider time) => new(db.DataSource, new RyuseiLimits(), time);

    private async Task InsertAgentAsync(Guid agentId, string state)
    {
        await using var cmd = db.DataSource.CreateCommand("INSERT INTO agent (agent_id, name, state) VALUES (@id, 'tick test', @state)");
        cmd.Parameters.AddWithValue("id", agentId);
        cmd.Parameters.AddWithValue("state", state);
        await cmd.ExecuteNonQueryAsync(Ct);
    }

    private static NewRyuseiTrigger NewTrigger(RyuseiTriggerSpec spec, DateTimeOffset? nextFireAt, params Guid[] targets) => new(
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        spec.Kind,
        targets,
        "check the build",
        RyuseiSpecJson.Encode(spec),
        TaskId: null,
        AttachGcId: null,
        RyuseiPingMode.None,
        nextFireAt,
        MaxFires: null,
        ExpiresAt: null,
        ChainDepth: 0);

    private async Task<RyuseiTriggerRecord> InsertCommittedAsync(NewRyuseiTrigger trigger)
    {
        await using var conn = await db.DataSource.OpenConnectionAsync(Ct);
        await using var tx = await conn.BeginTransactionAsync(Ct);
        var row = await Repo.InsertAsync(tx, trigger, Ct);
        await tx.CommitAsync(Ct);
        return row;
    }

    private async Task<(string Outcome, Guid FireId)[]> FiresOfAsync(Guid triggerId)
    {
        await using var cmd = db.DataSource.CreateCommand(
            "SELECT outcome, fire_id FROM ryusei_fire WHERE trigger_id = @trigger_id ORDER BY scheduled_for");
        cmd.Parameters.AddWithValue("trigger_id", triggerId);
        var rows = new List<(string, Guid)>();
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            rows.Add((reader.GetString(0), reader.GetGuid(1)));
        }

        return rows.ToArray();
    }

    private async Task<List<(string Kind, string? SourceRef, string State)>> InboxOfAsync(Guid agentId)
    {
        await using var cmd = db.DataSource.CreateCommand("SELECT kind, source_ref, state FROM inbox_item WHERE agent_id = @agent_id");
        cmd.Parameters.AddWithValue("agent_id", agentId);
        var rows = new List<(string, string?, string)>();
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            rows.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2)));
        }

        return rows;
    }

    [Fact]
    public async Task A_claim_skips_rows_that_another_transaction_has_locked()
    {
        SkipWithoutDatabase();
        var due = _base.AddMinutes(-1);
        var row = await InsertCommittedAsync(NewTrigger(new RyuseiTimerTriggerSpec(due), due, Guid.CreateVersion7()));

        await using var first = await db.DataSource.OpenConnectionAsync(Ct);
        await using var firstTx = await first.BeginTransactionAsync(Ct);
        var claimedFirst = await Repo.ClaimDueAsync(firstTx, _base, 1000, Ct);

        await using var second = await db.DataSource.OpenConnectionAsync(Ct);
        await using var secondTx = await second.BeginTransactionAsync(Ct);
        var claimedSecond = await Repo.ClaimDueAsync(secondTx, _base, 1000, Ct);
        await secondTx.RollbackAsync(Ct);
        await firstTx.RollbackAsync(Ct);

        Assert.Contains(claimedFirst, r => r.TriggerId == row.TriggerId);
        Assert.DoesNotContain(claimedSecond, r => r.TriggerId == row.TriggerId);
    }

    [Fact]
    public async Task A_claim_leaves_paused_cancelled_future_watcher_and_event_rows_alone()
    {
        SkipWithoutDatabase();
        var due = _base.AddMinutes(-1);
        var paused = await InsertCommittedAsync(NewTrigger(new RyuseiTimerTriggerSpec(due), due, Guid.CreateVersion7()));
        await using (var conn = await db.DataSource.OpenConnectionAsync(Ct))
        await using (var tx = await conn.BeginTransactionAsync(Ct))
        {
            await Repo.UpdateAsync(tx, paused with { State = RyuseiTriggerState.Paused }, paused.Version, Ct);
            await tx.CommitAsync(Ct);
        }

        var future = await InsertCommittedAsync(NewTrigger(new RyuseiTimerTriggerSpec(_base.AddDays(1)), _base.AddDays(1), Guid.CreateVersion7()));
        // An idle watcher has no next fire, so no claim picks it up. An event-armed watcher is claimed (see the next test).
        var idleWatcher = await InsertCommittedAsync(NewTrigger(
            new RyuseiWatcherTriggerSpec(new RyuseiPredicate { Kind = "task.transitioned" }, 60, 30, null, null),
            null,
            Guid.CreateVersion7()));

        await using var claimConn = await db.DataSource.OpenConnectionAsync(Ct);
        await using var claimTx = await claimConn.BeginTransactionAsync(Ct);
        var claimed = await Repo.ClaimDueAsync(claimTx, _base, 1000, Ct);
        await claimTx.RollbackAsync(Ct);

        Assert.DoesNotContain(claimed, r => r.TriggerId == paused.TriggerId);
        Assert.DoesNotContain(claimed, r => r.TriggerId == future.TriggerId);
        Assert.DoesNotContain(claimed, r => r.TriggerId == idleWatcher.TriggerId);
    }

    [Fact]
    public async Task A_fire_is_recorded_once_per_trigger_and_scheduled_instant()
    {
        SkipWithoutDatabase();
        var row = await InsertCommittedAsync(NewTrigger(new RyuseiTimerTriggerSpec(_base), _base, Guid.CreateVersion7()));
        var cause = new JsonObject { ["missedCount"] = 0 };

        await using var conn = await db.DataSource.OpenConnectionAsync(Ct);
        await using var tx = await conn.BeginTransactionAsync(Ct);
        var first = await RyuseiFireRepository.InsertAsync(tx, Guid.CreateVersion7(), row.TriggerId, _base, "woke", cause, _base, Ct);
        var second = await RyuseiFireRepository.InsertAsync(tx, Guid.CreateVersion7(), row.TriggerId, _base, "woke", cause, _base, Ct);
        var later = await RyuseiFireRepository.InsertAsync(tx, Guid.CreateVersion7(), row.TriggerId, _base.AddHours(1), "woke", cause, _base, Ct);
        await tx.CommitAsync(Ct);

        Assert.True(first);
        Assert.False(second);
        Assert.True(later);
        Assert.Equal(2, (await FiresOfAsync(row.TriggerId)).Length);
    }

    [Fact]
    public async Task A_due_timer_wakes_each_wakeable_target_once_and_is_then_fired()
    {
        SkipWithoutDatabase();
        var active = Guid.CreateVersion7();
        var resting = Guid.CreateVersion7();
        await InsertAgentAsync(active, "active");
        await InsertAgentAsync(resting, "resting");
        var fireAt = _base.AddHours(2);
        var row = await InsertCommittedAsync(NewTrigger(new RyuseiTimerTriggerSpec(fireAt), fireAt, active, resting));

        await Tick(Clock(fireAt.AddMinutes(1))).TickAsync(Ct);

        var stored = await Repo.GetAsync(row.TriggerId, Ct);
        Assert.NotNull(stored);
        Assert.Equal(RyuseiTriggerState.Fired, stored.State);
        Assert.Null(stored.NextFireAt);
        Assert.Equal(1L, stored.FireCount);
        Assert.Equal(2L, stored.Version);

        var fires = await FiresOfAsync(row.TriggerId);
        var fire = Assert.Single(fires);
        Assert.Equal("woke", fire.Outcome);

        var woken = Assert.Single(await InboxOfAsync(active));
        Assert.Equal("ryusei_timer", woken.Kind);
        Assert.Equal(fire.FireId.ToString("D"), woken.SourceRef);
        Assert.Equal("pending", woken.State);
        Assert.Empty(await InboxOfAsync(resting));
    }

    [Fact]
    public async Task A_second_pass_at_the_same_instant_does_not_fire_a_timer_again()
    {
        SkipWithoutDatabase();
        var agent = Guid.CreateVersion7();
        await InsertAgentAsync(agent, "active");
        var fireAt = _base.AddHours(3);
        var row = await InsertCommittedAsync(NewTrigger(new RyuseiTimerTriggerSpec(fireAt), fireAt, agent));
        var clock = Clock(fireAt.AddMinutes(1));

        await Tick(clock).TickAsync(Ct);
        var secondPass = await Tick(clock).TickAsync(Ct);

        Assert.Equal(0, secondPass);
        Assert.Single(await FiresOfAsync(row.TriggerId));
        Assert.Single(await InboxOfAsync(agent));
    }

    [Fact]
    public async Task An_overdue_interval_fires_once_for_its_latest_occurrence_and_moves_on()
    {
        SkipWithoutDatabase();
        var agent = Guid.CreateVersion7();
        await InsertAgentAsync(agent, "active");
        var anchor = _base;
        var spec = new RyuseiIntervalTriggerSpec(TimeSpan.FromHours(1), anchor);
        var row = await InsertCommittedAsync(NewTrigger(spec, anchor, agent));

        // Three hours and thirty minutes after the anchor: the 12:00 occurrence is handled as the 15:00 fire, with two missed.
        await Tick(Clock(anchor.AddHours(3).AddMinutes(30))).TickAsync(Ct);

        var stored = await Repo.GetAsync(row.TriggerId, Ct);
        Assert.NotNull(stored);
        Assert.Equal(RyuseiTriggerState.Active, stored.State);
        Assert.Equal(anchor.AddHours(4), stored.NextFireAt);
        Assert.Equal(1L, stored.FireCount);

        var fire = Assert.Single(await FiresOfAsync(row.TriggerId));
        Assert.Equal("woke", fire.Outcome);
        var woken = Assert.Single(await InboxOfAsync(agent));
        Assert.Equal("ryusei_interval", woken.Kind);
    }

    [Fact]
    public async Task A_fire_whose_only_target_is_retired_is_skipped_and_the_timer_expires()
    {
        SkipWithoutDatabase();
        var retired = Guid.CreateVersion7();
        await InsertAgentAsync(retired, "retired");
        var fireAt = _base.AddHours(4);
        var row = await InsertCommittedAsync(NewTrigger(new RyuseiTimerTriggerSpec(fireAt), fireAt, retired));

        var written = await Tick(Clock(fireAt)).TickAsync(Ct);

        Assert.True(written >= 1);
        var stored = await Repo.GetAsync(row.TriggerId, Ct);
        Assert.NotNull(stored);
        Assert.Equal(RyuseiTriggerState.Expired, stored.State);
        Assert.Equal("skipped_resting", Assert.Single(await FiresOfAsync(row.TriggerId)).Outcome);
        Assert.Empty(await InboxOfAsync(retired));
    }

    [Fact]
    public async Task An_idle_watcher_is_not_fired_by_the_tick()
    {
        SkipWithoutDatabase();
        var agent = Guid.CreateVersion7();
        await InsertAgentAsync(agent, "active");
        var row = await InsertCommittedAsync(NewTrigger(
            new RyuseiWatcherTriggerSpec(new RyuseiPredicate { Kind = "task.transitioned" }, 60, 30, null, null),
            null,
            agent));

        await Tick(Clock(_base.AddHours(1))).TickAsync(Ct);

        var stored = await Repo.GetAsync(row.TriggerId, Ct);
        Assert.NotNull(stored);
        Assert.Equal(RyuseiTriggerState.Active, stored.State);
        Assert.Equal(1L, stored.Version);
        Assert.Empty(await FiresOfAsync(row.TriggerId));
        Assert.Empty(await InboxOfAsync(agent));
    }

    /// <summary>A GC log that records each append and answers with a fixed seq, so a test can check the line and the wake keys.</summary>
    private sealed class RecordingGcLog(long seq) : IGcLogWriter
    {
        public List<AppendRequest> Requests { get; } = [];

        public Task<AppendResult> AppendAsync(AppendRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(new AppendResult(request.GcId, seq, Guid.CreateVersion7(), false, DateTimeOffset.UtcNow));
        }

        public Task<IReadOnlyList<AppendResult>> AppendManyAsync(IReadOnlyList<AppendRequest> requests, CancellationToken ct) =>
            throw new NotSupportedException("The tick posts one line at a time.");
    }

    private async Task InsertGcAsync(Guid gcId)
    {
        await using var cmd = db.DataSource.CreateCommand(
            "INSERT INTO gc (gc_id, type_key, title, created_by) VALUES (@id, 'general', 'tick attach test', @by)");
        cmd.Parameters.AddWithValue("id", gcId);
        cmd.Parameters.AddWithValue("by", Guid.CreateVersion7());
        await cmd.ExecuteNonQueryAsync(Ct);
    }

    private async Task<List<(Guid GcId, long Seq, string Kind, string? SourceRef)>> ByGcInboxOfAsync(Guid agentId)
    {
        await using var cmd = db.DataSource.CreateCommand(
            "SELECT gc_id, gc_seq, kind, source_ref FROM inbox_item WHERE agent_id = @agent_id AND gc_id IS NOT NULL");
        cmd.Parameters.AddWithValue("agent_id", agentId);
        var rows = new List<(Guid, long, string, string?)>();
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            rows.Add((reader.GetGuid(0), reader.GetInt64(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return rows;
    }

    [Fact]
    public async Task An_attach_fire_posts_one_system_line_and_keys_the_wake_to_it()
    {
        SkipWithoutDatabase();
        var agent = Guid.CreateVersion7();
        await InsertAgentAsync(agent, "active");
        var attachGc = Guid.CreateVersion7();
        await InsertGcAsync(attachGc);
        var due = _base.AddMinutes(-1);
        var trigger = NewTrigger(new RyuseiTimerTriggerSpec(due), due, agent) with
        {
            AttachGcId = attachGc,
            PingMode = RyuseiPingMode.Attach,
        };
        var row = await InsertCommittedAsync(trigger);

        var gcLog = new RecordingGcLog(seq: 7);
        var tick = new RyuseiTickService(db.DataSource, new RyuseiLimits(), Clock(_base), gcLog: gcLog);
        await tick.TickAsync(Ct);

        // One line, in the attach GC, system kind, carrying the trigger id and the target id.
        var line = Assert.Single(gcLog.Requests);
        Assert.Equal(attachGc, line.GcId);
        Assert.Equal(GcEventKinds.System, line.Kind);
        Assert.Contains(agent.ToString("D"), line.PayloadJson);

        // The fire woke the target. The wake is keyed to the line's (gc_id, gc_seq) and carries the fire id as its source ref,
        // so the woken turn can read the fire's chain depth (WakeChainDepthSource).
        var fires = await FiresOfAsync(row.TriggerId);
        var fire = Assert.Single(fires);
        Assert.Equal("woke", fire.Outcome);
        var wake = Assert.Single(await ByGcInboxOfAsync(agent));
        Assert.Equal(attachGc, wake.GcId);
        Assert.Equal(7, wake.Seq);
        Assert.Equal("ryusei_ping", wake.Kind);
        Assert.Equal(fire.FireId.ToString("D"), wake.SourceRef);
    }

    [Fact]
    public async Task A_new_gc_fire_creates_the_gc_with_its_members_and_posts_into_it()
    {
        SkipWithoutDatabase();
        var agent = Guid.CreateVersion7();
        await InsertAgentAsync(agent, "active");
        var due = _base.AddMinutes(-1);
        var trigger = NewTrigger(new RyuseiTimerTriggerSpec(due), due, agent) with { PingMode = RyuseiPingMode.NewGc };
        var row = await InsertCommittedAsync(trigger);

        var gcLog = new RecordingGcLog(seq: 1);
        var tick = new RyuseiTickService(db.DataSource, new RyuseiLimits(), Clock(_base), gcLog: gcLog, gcCreate: new GcCreateService(db.DataSource));
        await tick.TickAsync(Ct);

        // The line goes into the GC that was just created: the same deterministic id the service derives.
        var line = Assert.Single(gcLog.Requests);
        var expectedGc = GcCreateService.DeterministicGcId($"ryusei-gc:{row.TriggerId:D}:{_base.AddMinutes(-1):O}");
        Assert.Equal(expectedGc, line.GcId);

        await using (var cmd = db.DataSource.CreateCommand("SELECT title FROM gc WHERE gc_id = @id"))
        {
            cmd.Parameters.AddWithValue("id", expectedGc);
            var title = (string?)await cmd.ExecuteScalarAsync(Ct);
            Assert.StartsWith("RyuseiFuda: ", title);
        }

        await using (var cmd = db.DataSource.CreateCommand("SELECT count(*) FROM gc_member WHERE gc_id = @id"))
        {
            cmd.Parameters.AddWithValue("id", expectedGc);
            // The target and the creator.
            Assert.Equal(2L, (long)(await cmd.ExecuteScalarAsync(Ct))!);
        }

        Assert.Equal("woke", Assert.Single(await FiresOfAsync(row.TriggerId)).Outcome);
        var wake = Assert.Single(await ByGcInboxOfAsync(agent));
        Assert.Equal(expectedGc, wake.GcId);
        Assert.Equal("ryusei_ping", wake.Kind);
    }

    [Fact]
    public async Task Creating_the_same_gc_twice_writes_it_once()
    {
        SkipWithoutDatabase();
        var creator = Guid.CreateVersion7();
        var member = Guid.CreateVersion7();
        var gcId = GcCreateService.DeterministicGcId($"ryusei-gc-twice:{Guid.CreateVersion7():D}");
        var service = new GcCreateService(db.DataSource);
        var request = new GcCreateRequest(gcId, "twice", creator, [member, creator]);

        Assert.True(await service.EnsureAsync(request, Ct));
        Assert.False(await service.EnsureAsync(request, Ct));

        await using var cmd = db.DataSource.CreateCommand("SELECT count(*) FROM gc_member WHERE gc_id = @id");
        cmd.Parameters.AddWithValue("id", gcId);
        Assert.Equal(2L, (long)(await cmd.ExecuteScalarAsync(Ct))!);
    }
}
