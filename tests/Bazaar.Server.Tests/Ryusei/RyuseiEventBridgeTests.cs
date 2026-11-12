using Bazaar.RyuseiFuda.Orrery;
using Bazaar.Server.Tests.Kanban.Data;
using Bazaar.Testing.Postgres;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Bazaar.Server.Tests.Ryusei;

/// <summary>
/// The event bridge on real PostgreSQL (doc 38, WP-R6): an event trigger fires once on its first match, a watcher is armed with a
/// debounce deadline and fired by the tick, and a GC-sourced event never reaches a trigger that names no GC. Uses the Kanban
/// fixture, which migrates every namespace. This container has no Docker, so these tests are not run here; CI is the verifier.
/// </summary>
public sealed class RyuseiEventBridgeTests(KanbanDb db) : IClassFixture<KanbanDb>
{
    private static readonly DateTimeOffset _base = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private RyuseiTriggerRepository Repo => new(db.DataSource);

    private RyuseiEventBridge Bridge => new(db.DataSource);

    private static RyuseiEventEnvelope TaskEvent(Guid projectId, string kind = "task.transitioned") =>
        RyuseiEventMapping.FromTaskEvent(kind, projectId, Guid.CreateVersion7(), "{}")!;

    private async Task InsertAgentAsync(Guid agentId, string state)
    {
        await using var cmd = db.DataSource.CreateCommand("INSERT INTO agent (agent_id, name, state) VALUES (@id, 'bridge test', @state)");
        cmd.Parameters.AddWithValue("id", agentId);
        cmd.Parameters.AddWithValue("state", state);
        await cmd.ExecuteNonQueryAsync(Ct);
    }

    private static NewRyuseiTrigger NewTrigger(RyuseiTriggerSpec spec, Guid projectId, DateTimeOffset? nextFireAt, Guid agent) => new(
        Guid.CreateVersion7(),
        projectId,
        Guid.CreateVersion7(),
        spec.Kind,
        [agent],
        "watch the board",
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

    private async Task<List<(string Kind, string State)>> InboxOfAsync(Guid agentId)
    {
        await using var cmd = db.DataSource.CreateCommand("SELECT kind, state FROM inbox_item WHERE agent_id = @agent_id");
        cmd.Parameters.AddWithValue("agent_id", agentId);
        var rows = new List<(string, string)>();
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            rows.Add((reader.GetString(0), reader.GetString(1)));
        }

        return rows;
    }

    [Fact]
    public async Task An_event_trigger_fires_once_on_its_first_match_and_then_ignores_later_matches()
    {
        SkipWithoutDatabase();
        var project = Guid.CreateVersion7();
        var agent = Guid.CreateVersion7();
        await InsertAgentAsync(agent, "active");
        var row = await InsertCommittedAsync(NewTrigger(
            new RyuseiEventTriggerSpec(new RyuseiPredicate { Kind = "task.transitioned" }),
            project,
            null,
            agent));

        var first = await Bridge.HandleAsync(TaskEvent(project), _base, Ct);
        var second = await Bridge.HandleAsync(TaskEvent(project), _base.AddSeconds(5), Ct);

        Assert.Equal(1, first.Fired);
        Assert.Equal(0, second.Fired);
        var stored = await Repo.GetAsync(row.TriggerId, Ct);
        Assert.NotNull(stored);
        Assert.Equal(RyuseiTriggerState.Fired, stored.State);
        Assert.Equal(1L, stored.FireCount);
        var fires = await FiresOfAsync(row.TriggerId);
        Assert.Single(fires);
        Assert.Equal("woke", fires[0].Outcome);
        var inbox = await InboxOfAsync(agent);
        Assert.Single(inbox);
        Assert.Equal("ryusei_event", inbox[0].Kind);
    }

    [Fact]
    public async Task An_event_refused_by_the_shared_wake_cap_stays_active_and_writes_no_inbox_item()
    {
        SkipWithoutDatabase();
        var project = Guid.CreateVersion7();
        var agent = Guid.CreateVersion7();
        await InsertAgentAsync(agent, "active");
        var row = await InsertCommittedAsync(NewTrigger(
            new RyuseiEventTriggerSpec(new RyuseiPredicate { Kind = "task.transitioned" }),
            project,
            null,
            agent));

        // A cap of zero refuses every wake, so the event must be recorded as refused and the trigger kept for a later match.
        var capped = new RyuseiEventBridge(db.DataSource, new RyuseiLimits { MaxAgentWakesPerHour = 0 });
        var result = await capped.HandleAsync(TaskEvent(project), _base, Ct);

        Assert.Equal(1, result.Fired);
        var stored = await Repo.GetAsync(row.TriggerId, Ct);
        Assert.NotNull(stored);
        Assert.Equal(RyuseiTriggerState.Active, stored.State);
        Assert.Equal(0L, stored.FireCount);
        Assert.Equal(1, stored.ConsecutiveRefusals);
        var fires = await FiresOfAsync(row.TriggerId);
        Assert.Single(fires);
        Assert.Equal("refused_cap", fires[0].Outcome);
        Assert.Empty(await InboxOfAsync(agent));
    }

    [Fact]
    public async Task An_event_in_another_project_does_not_touch_the_trigger()
    {
        SkipWithoutDatabase();
        var project = Guid.CreateVersion7();
        var agent = Guid.CreateVersion7();
        await InsertAgentAsync(agent, "active");
        var row = await InsertCommittedAsync(NewTrigger(
            new RyuseiEventTriggerSpec(new RyuseiPredicate { Kind = "task.transitioned" }),
            project,
            null,
            agent));

        var result = await Bridge.HandleAsync(TaskEvent(Guid.CreateVersion7()), _base, Ct);

        Assert.Equal(0, result.Fired + result.Armed + result.Expired);
        var stored = await Repo.GetAsync(row.TriggerId, Ct);
        Assert.NotNull(stored);
        Assert.Equal(RyuseiTriggerState.Active, stored.State);
        Assert.Empty(await FiresOfAsync(row.TriggerId));
    }

    [Fact]
    public async Task A_watcher_is_armed_by_a_match_and_the_tick_fires_it_once_the_debounce_passes()
    {
        SkipWithoutDatabase();
        var project = Guid.CreateVersion7();
        var agent = Guid.CreateVersion7();
        await InsertAgentAsync(agent, "active");
        var row = await InsertCommittedAsync(NewTrigger(
            new RyuseiWatcherTriggerSpec(new RyuseiPredicate { Kind = "task.transitioned" }, 60, 30, null, null),
            project,
            null,
            agent));

        var armed = await Bridge.HandleAsync(TaskEvent(project), _base, Ct);
        var burst = await Bridge.HandleAsync(TaskEvent(project), _base.AddSeconds(10), Ct);

        Assert.Equal(1, armed.Armed);
        Assert.Equal(0, burst.Armed);
        var pending = await Repo.GetAsync(row.TriggerId, Ct);
        Assert.NotNull(pending);
        Assert.Equal(_base.AddSeconds(30), pending.NextFireAt);
        Assert.Equal(RyuseiTriggerState.Active, pending.State);

        // The shared test database can hold other due rows, so each assertion reads this trigger's rows only.
        await new RyuseiTickService(db.DataSource, new RyuseiLimits(), new FakeTimeProvider(_base.AddSeconds(29))).TickAsync(Ct);
        Assert.Empty(await FiresOfAsync(row.TriggerId));

        await new RyuseiTickService(db.DataSource, new RyuseiLimits(), new FakeTimeProvider(_base.AddSeconds(31))).TickAsync(Ct);
        var fires = await FiresOfAsync(row.TriggerId);
        Assert.Single(fires);
        Assert.Equal("woke", fires[0].Outcome);
        var idle = await Repo.GetAsync(row.TriggerId, Ct);
        Assert.NotNull(idle);
        Assert.Equal(RyuseiTriggerState.Active, idle.State);
        Assert.Null(idle.NextFireAt);
        var inbox = await InboxOfAsync(agent);
        Assert.Single(inbox);
        Assert.Equal("ryusei_watcher", inbox[0].Kind);
    }

    [Fact]
    public async Task A_gc_message_without_a_gc_filter_does_not_reach_a_trigger()
    {
        SkipWithoutDatabase();
        var agent = Guid.CreateVersion7();
        await InsertAgentAsync(agent, "active");
        var row = await InsertCommittedAsync(NewTrigger(
            new RyuseiEventTriggerSpec(new RyuseiPredicate { Kind = "gc.message" }),
            Guid.CreateVersion7(),
            null,
            agent));
        var message = RyuseiEventMapping.FromGcEvent("message", Guid.CreateVersion7(), "agent", agent, 1)!;

        var result = await Bridge.HandleAsync(message, _base, Ct);

        Assert.Equal(0, result.Fired);
        var stored = await Repo.GetAsync(row.TriggerId, Ct);
        Assert.NotNull(stored);
        Assert.Equal(RyuseiTriggerState.Active, stored.State);
    }
}
