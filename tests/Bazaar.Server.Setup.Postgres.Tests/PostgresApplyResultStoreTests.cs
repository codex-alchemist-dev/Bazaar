using Bazaar.Server.Setup;
using Bazaar.Server.Setup.Postgres;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Setup.Postgres.Tests;

public sealed class PostgresApplyResultStoreTests
{
    private static SetupApplyResult Result(SetupExitCode code, string hash, params string[] steps) =>
        new(code, hash, steps, [new SetupValidationIssue(SetupIssueSeverity.Hold, "backups.target", "needs a target")]);

    [Fact]
    public async Task A_saved_result_comes_back_after_a_restart()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SetupDatabase.CreateAsync(ct);
        await new PostgresApplyResultStore(db.DataSource).SaveAsync("hash-1", Result(SetupExitCode.Held, "hash-1", "database.schema"), ct);

        var read = await new PostgresApplyResultStore(db.DataSource).TryGetAsync("hash-1", ct);

        Assert.NotNull(read);
        Assert.Equal(SetupExitCode.Held, read.ExitCode);
        Assert.Equal("hash-1", read.PlanHash);
        Assert.Equal(["database.schema"], read.AppliedStepIds);
        var issue = Assert.Single(read.Issues);
        Assert.Equal(new SetupValidationIssue(SetupIssueSeverity.Hold, "backups.target", "needs a target"), issue);
    }

    [Fact]
    public async Task An_unknown_hash_reads_as_null()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SetupDatabase.CreateAsync(ct);

        Assert.Null(await new PostgresApplyResultStore(db.DataSource).TryGetAsync("nope", ct));
    }

    [Fact]
    public async Task The_first_saved_result_for_a_hash_is_the_one_kept()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SetupDatabase.CreateAsync(ct);
        var store = new PostgresApplyResultStore(db.DataSource);

        await store.SaveAsync("hash-2", Result(SetupExitCode.Ok, "hash-2", "a", "b"), ct);
        await store.SaveAsync("hash-2", Result(SetupExitCode.ApplyFailedRolledBack, "hash-2"), ct);

        var read = await store.TryGetAsync("hash-2", ct);
        Assert.Equal(SetupExitCode.Ok, read!.ExitCode);
        Assert.Equal(["a", "b"], read.AppliedStepIds);
    }

    [Fact]
    public async Task Concurrent_saves_of_one_hash_keep_exactly_one_row()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SetupDatabase.CreateAsync(ct);
        var store = new PostgresApplyResultStore(db.DataSource);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => store.SaveAsync("hash-3", Result(SetupExitCode.Ok, "hash-3", $"s{i}"), ct)));

        await using var cmd = db.DataSource.CreateCommand("SELECT count(*) FROM setup_apply_result WHERE plan_hash = 'hash-3'");
        Assert.Equal(1L, await cmd.ExecuteScalarAsync(ct));
    }
}
