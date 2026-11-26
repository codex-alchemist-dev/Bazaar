using Bazaar.Server.AgentSafety.Anomaly;
using Bazaar.Server.AgentSafety.AutoContainment;
using Bazaar.Server.AgentSafety.Persistence;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.AgentSafety.Tests;

public sealed class PostgresContainmentRecordStoreTests
{
    private static ContainmentRecord Record(string agent, ContainmentRecordState state, DateTimeOffset at) =>
        new(Guid.NewGuid(), agent, "canary_hit", AnomalySeverity.High, at, at.AddMinutes(15), state);

    [Fact]
    public async Task One_in_force_record_per_agent_and_the_state_moves_once()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("autocontain", ServerMigrations.MigrateAsync, ct);
        var store = new PostgresContainmentRecordStore(db.DataSource);
        var at = new DateTimeOffset(2026, 11, 19, 9, 0, 0, TimeSpan.Zero);
        var first = Record("agent-a", ContainmentRecordState.InForce, at);

        Assert.True(await store.TryAddAsync(first, ct));
        Assert.False(await store.TryAddAsync(Record("agent-a", ContainmentRecordState.InForce, at.AddSeconds(1)), ct));
        Assert.True(await store.TryTransitionAsync(first.Id, ContainmentRecordState.InForce, ContainmentRecordState.Undone, ct));
        Assert.False(await store.TryTransitionAsync(first.Id, ContainmentRecordState.InForce, ContainmentRecordState.Undone, ct));

        var latest = await store.GetLatestAsync("agent-a", ct);
        Assert.Equal(ContainmentRecordState.Undone, latest?.State);
    }

    [Fact]
    public async Task Elapsed_windows_expire()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("autocontain", ServerMigrations.MigrateAsync, ct);
        var store = new PostgresContainmentRecordStore(db.DataSource);
        var at = new DateTimeOffset(2026, 11, 19, 9, 0, 0, TimeSpan.Zero);
        await store.TryAddAsync(Record("agent-a", ContainmentRecordState.InForce, at), ct);

        Assert.Equal(0, await store.ExpireDueAsync(at.AddMinutes(14), ct));
        Assert.Equal(1, await store.ExpireDueAsync(at.AddMinutes(15), ct));
        Assert.Equal(ContainmentRecordState.WindowExpired, (await store.GetLatestAsync("agent-a", ct))?.State);
    }
}
