using Bazaar.Server.Security.KillSwitch;
using Bazaar.Server.Security.KillSwitch.Postgres;
using Bazaar.Server.Tests.Security.Budgets.Postgres;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Tests.Security.KillSwitch.Postgres;

/// <summary>PostgresContainmentStateStore against a real PostgreSQL: add keeps the first record, remove, and load (agent_safety 0003).</summary>
public sealed class PostgresContainmentStateStoreTests
{
    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private static readonly DateTimeOffset _t0 = new(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Contained_agents_round_trip_and_a_repeat_add_keeps_the_first_record()
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);
        var store = new PostgresContainmentStateStore(db.DataSource);
        var first = new ContainedAgent("agent-a", _t0.AddTicks(1234560), "system:containment");

        await store.AddAsync(first, Ct);
        await store.AddAsync(new ContainedAgent("agent-a", _t0.AddMinutes(1), "other"), Ct);
        await store.AddAsync(new ContainedAgent("agent-b", _t0, "system:containment"), Ct);

        var loaded = await store.LoadAsync(Ct);
        Assert.Equal(["agent-a", "agent-b"], loaded.Select(a => a.AgentId));
        Assert.Equal(first, loaded[0]);
    }

    [Fact]
    public async Task Removing_an_agent_leaves_the_others_and_ignores_a_missing_one()
    {
        SkipWithoutDatabase();
        await using var db = await BudgetTestDb.CreateAsync(Ct);
        var store = new PostgresContainmentStateStore(db.DataSource);
        await store.AddAsync(new ContainedAgent("agent-a", _t0, "system:containment"), Ct);
        await store.AddAsync(new ContainedAgent("agent-b", _t0, "system:containment"), Ct);

        await store.RemoveAsync("agent-a", Ct);
        await store.RemoveAsync("agent-missing", Ct);

        Assert.Equal(["agent-b"], (await store.LoadAsync(Ct)).Select(a => a.AgentId));
    }
}
