using Bazaar.Server.Data.Migrations;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.CompanyBuild.State.Tests;

/// <summary>Database-backed tests. They run only when <c>BAZAAR_TEST_PG</c> names a server; without it each one skips.</summary>
public sealed class PostgresCompanyBuildStateStoreTests : PgFixture, IAsyncLifetime
{
    private static readonly DateTimeOffset _now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    private NpgsqlDataSource? _pool;

    private static bool HasDatabase => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PgSource.EnvConnectionString));

    public override async ValueTask InitializeAsync()
    {
        if (!HasDatabase)
            return;
        await base.InitializeAsync();
        _pool = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(ConnectionString) { MaxPoolSize = 12 }.ConnectionString);
        var sources = new[] { new EmbeddedMigrationSource(typeof(MigrationRunner).Assembly), new EmbeddedMigrationSource(typeof(CompanyBuildStateComponent).Assembly) };
        var source = new InMemoryMigrationSource(sources.SelectMany(s => s.Namespaces().SelectMany(s.Load)));
        await new MigrationRunner(_pool, new MigrationOptions(16)).MigrateAsync(source, "linux", CancellationToken.None);
    }

    private PostgresCompanyBuildStateStore Store()
    {
        Assert.SkipUnless(HasDatabase, "BAZAAR_TEST_PG is not set: no test PostgreSQL available.");
        return new PostgresCompanyBuildStateStore(_pool!);
    }

    private static BuildRow Running(string key) => new(Guid.NewGuid(), 1, "running", Guid.NewGuid(), _now, null, key, 0, null);

    [Fact]
    public async Task The_selection_starts_empty_at_revision_zero_and_commits_by_compare_and_set()
    {
        var store = Store();
        var start = await store.GetSelectionAsync();
        Assert.Equal(0, start.Revision);

        var actor = Guid.NewGuid();
        var approval = Guid.NewGuid();
        var next = await store.TryCommitSelectionAsync(0, ["core", "web"], new Dictionary<string, Guid> { ["web"] = approval }, actor, _now);

        Assert.Equal(1, next!.Revision);
        Assert.Null(await store.TryCommitSelectionAsync(0, ["core"], new Dictionary<string, Guid>(), actor, _now));
        var read = await store.GetSelectionAsync();
        Assert.Equal(["core", "web"], read.SelectedModuleIds);
        Assert.Equal(approval, read.PendingApprovals["web"]);
        Assert.Equal(actor, read.UpdatedBy);

        Assert.True(await store.TrySetPendingApprovalsAsync(1, new Dictionary<string, Guid>()));
        Assert.False(await store.TrySetPendingApprovalsAsync(0, new Dictionary<string, Guid>()));
    }

    [Fact]
    public async Task Begin_build_reports_a_duplicate_key_and_a_second_running_build()
    {
        var store = Store();
        await store.FailRunningBuildsAsync(_now, "reset");
        var first = Running("pg-" + Guid.NewGuid());

        Assert.Equal(BuildBeginOutcome.Started, (await store.TryBeginBuildAsync(first)).Outcome);

        var dup = await store.TryBeginBuildAsync(Running(first.IdempotencyKey));
        Assert.Equal(BuildBeginOutcome.DuplicateKey, dup.Outcome);
        Assert.Equal(first.Id, dup.Existing!.Id);

        Assert.Equal(BuildBeginOutcome.AlreadyRunning, (await store.TryBeginBuildAsync(Running("pg-" + Guid.NewGuid()))).Outcome);

        await store.CompleteBuildAsync(first.Id, true, _now.AddMinutes(1), 2, "done");
        var done = (await store.GetBuildByKeyAsync(first.IdempotencyKey))!;
        Assert.Equal("succeeded", done.Status);
        Assert.Equal(2, done.Attempts);
        Assert.Equal(BuildBeginOutcome.Started, (await store.TryBeginBuildAsync(Running("pg-" + Guid.NewGuid()))).Outcome);
    }

    [Fact]
    public async Task Concurrent_begins_start_exactly_one_build()
    {
        var store = Store();
        await store.FailRunningBuildsAsync(_now, "reset");

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => store.TryBeginBuildAsync(Running("pg-race-" + Guid.NewGuid()))));

        Assert.Equal(1, results.Count(r => r.Outcome == BuildBeginOutcome.Started));
        Assert.Equal(7, results.Count(r => r.Outcome == BuildBeginOutcome.AlreadyRunning));
    }

    [Fact]
    public async Task Builds_list_newest_first_and_failed_running_rows_are_closed()
    {
        var store = Store();
        await store.FailRunningBuildsAsync(_now, "reset");
        var row = Running("pg-list-" + Guid.NewGuid());
        await store.TryBeginBuildAsync(row);

        Assert.Equal(1, await store.FailRunningBuildsAsync(_now.AddMinutes(1), "abandoned"));

        var list = await store.ListBuildsAsync(10);
        var failed = list.Single(b => b.Id == row.Id);
        Assert.Equal("failed", failed.Status);
        Assert.Equal("abandoned", failed.ResultSummary);
    }
}
