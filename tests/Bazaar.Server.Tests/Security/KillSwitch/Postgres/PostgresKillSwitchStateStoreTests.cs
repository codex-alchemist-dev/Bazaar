using Bazaar.Server.Security.KillSwitch;
using Bazaar.Server.Security.KillSwitch.Postgres;
using Bazaar.Server.Tests.Security.Budgets.Postgres;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Tests.Security.KillSwitch.Postgres;

/// <summary>PostgresKillSwitchStateStore against a real PostgreSQL: round trips, replacement and clearing (agent_safety 0003).</summary>
public sealed class PostgresKillSwitchStateStoreTests
{
    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private static readonly DateTimeOffset _t0 = new(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Global_stop_round_trips_through_the_database()
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);
        var store = new PostgresKillSwitchStateStore(db.DataSource);
        var stop = new KillSwitchStop(KillSwitchScope.Global, null, "owner-1", "Freeze", _t0.AddTicks(1234560));

        await store.SetGlobalAsync(stop, Ct);
        var loaded = await store.LoadAsync(Ct);
        Assert.Equal(stop, loaded.Global);

        await store.ClearGlobalAsync(Ct);
        Assert.Null((await store.LoadAsync(Ct)).Global);
    }

    [Fact]
    public async Task Setting_the_global_stop_twice_keeps_one_row()
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);
        var store = new PostgresKillSwitchStateStore(db.DataSource);

        await store.SetGlobalAsync(new KillSwitchStop(KillSwitchScope.Global, null, "owner-1", "First", _t0), Ct);
        await store.SetGlobalAsync(new KillSwitchStop(KillSwitchScope.Global, null, "owner-2", "Second", _t0.AddMinutes(1)), Ct);

        var loaded = await store.LoadAsync(Ct);
        Assert.Equal("Second", loaded.Global!.Reason);
        Assert.Equal("owner-2", loaded.Global.ActorId);
    }

    [Fact]
    public async Task Agent_stops_round_trip_replace_and_clear()
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);
        var store = new PostgresKillSwitchStateStore(db.DataSource);
        await store.SetAgentStopAsync(new KillSwitchStop(KillSwitchScope.Agent, "agent-b", "owner-1", "B", _t0), Ct);
        var replacement = new KillSwitchStop(KillSwitchScope.Agent, "agent-a", "owner-2", "A again", _t0.AddMinutes(2));
        await store.SetAgentStopAsync(new KillSwitchStop(KillSwitchScope.Agent, "agent-a", "owner-1", "A", _t0), Ct);

        await store.SetAgentStopAsync(replacement, Ct);
        var loaded = await store.LoadAsync(Ct);
        Assert.Equal(["agent-a", "agent-b"], loaded.AgentStops.Select(s => s.AgentId));
        Assert.Equal(replacement, loaded.AgentStops[0]);

        await store.ClearAgentStopAsync("agent-a", Ct);
        Assert.Equal(["agent-b"], (await store.LoadAsync(Ct)).AgentStops.Select(s => s.AgentId));
    }
}
