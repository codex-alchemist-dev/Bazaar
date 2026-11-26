using Bazaar.Server.AgentSafety.Drill.Persistence;
using Bazaar.Server.Data.Migrations;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.AgentSafety.Drill.Tests;

public sealed class DrillResultStoreTests
{
    private static DrillResult Sample(DateTimeOffset startedAt, bool pass) => new(
        Guid.CreateVersion7(startedAt),
        startedAt,
        TimeSpan.FromMilliseconds(1234),
        pass
            ? [new DrillStepResult("baseline", true, "Passed."), new DrillStepResult("release", true, "Passed.")]
            : [new DrillStepResult("containment", false, "Egress was not blocked."), new DrillStepResult("cleanup", true, "Released the drill agent after the run.")]);

    [Fact]
    public async Task The_in_memory_store_returns_the_latest_run_by_start_time()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryDrillResultStore();
        var older = Sample(new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero), pass: true);
        var newer = Sample(new DateTimeOffset(2026, 11, 19, 9, 0, 0, TimeSpan.Zero), pass: false);

        await store.AddAsync(newer, ct);
        await store.AddAsync(older, ct);

        var latest = await store.LatestAsync(ct);
        Assert.Equal(newer, latest);
    }

    [Fact]
    public async Task The_in_memory_store_is_empty_before_the_first_run()
    {
        Assert.Null(await new InMemoryDrillResultStore().LatestAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_status_port_reads_the_latest_run_from_the_store()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryDrillResultStore();
        var run = Sample(new DateTimeOffset(2026, 11, 19, 9, 0, 0, TimeSpan.Zero), pass: true);
        await store.AddAsync(run, ct);

        Assert.Equal(run, await new DrillStatus(store).GetLatestAsync(ct));
    }

    [Fact]
    public async Task A_run_round_trips_through_the_postgres_store()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("drill", ServerMigrations.MigrateAsync, ct);
        var store = new PostgresDrillResultStore(db.DataSource);
        Assert.Null(await store.LatestAsync(ct));

        var run = Sample(new DateTimeOffset(2026, 11, 19, 9, 0, 0, TimeSpan.Zero), pass: false);
        await store.AddAsync(run, ct);
        var read = await new PostgresDrillResultStore(db.DataSource).LatestAsync(ct);

        Assert.NotNull(read);
        Assert.Equal(run.Id, read.Id);
        Assert.Equal(run.StartedAt, read.StartedAt);
        Assert.Equal(run.Duration, read.Duration);
        Assert.Equal(run.Steps, read.Steps);
        Assert.False(read.Passed);
    }
}
