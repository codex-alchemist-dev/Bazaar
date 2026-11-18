using Bazaar.Contracts.Coding;
using Bazaar.Server.CodingStore.Data;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.CodingStore.Tests;

public sealed class CheckRunStoreTests(CodingDb db) : IClassFixture<CodingDb>
{
    private static readonly DateTimeOffset _t0 = new(2026, 11, 18, 10, 0, 0, TimeSpan.Zero);

    private static CheckRunRecord Run(string commit, DateTimeOffset at) => new(
        Guid.NewGuid(), commit, "base1", 0, 120, 1, 3, 130, [CheckFlag.SkipAdded, CheckFlag.TestCountDropped], at);

    private static void RequireDb() => Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    [Fact]
    public async Task Append_then_get_round_trips_including_flags()
    {
        RequireDb();
        var run = Run("c-" + Guid.NewGuid(), _t0);
        var store = new PostgresCheckRunStore(db.DataSource);
        await store.AppendAsync(run, TestContext.Current.CancellationToken);

        var back = await store.GetAsync(run.Id, TestContext.Current.CancellationToken);

        Assert.NotNull(back);
        Assert.Equal(run with { Flags = [] }, back with { Flags = [] });
        Assert.Equal(run.Flags, back.Flags);
    }

    [Fact]
    public async Task Nulls_round_trip_and_a_missing_run_is_null()
    {
        RequireDb();
        var run = new CheckRunRecord(Guid.NewGuid(), "c-" + Guid.NewGuid(), null, null, null, null, null, null, [], _t0);
        var store = new PostgresCheckRunStore(db.DataSource);
        await store.AppendAsync(run, TestContext.Current.CancellationToken);

        var back = await store.GetAsync(run.Id, TestContext.Current.CancellationToken);

        Assert.Null(back!.BaseCommit);
        Assert.Null(back.BuildExitCode);
        Assert.Empty(back.Flags);
        Assert.Null(await store.GetAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task List_by_commit_is_newest_first_and_scoped_to_the_commit()
    {
        RequireDb();
        var commit = "c-" + Guid.NewGuid();
        var store = new PostgresCheckRunStore(db.DataSource);
        var older = Run(commit, _t0);
        var newer = Run(commit, _t0.AddMinutes(5));
        await store.AppendAsync(older, TestContext.Current.CancellationToken);
        await store.AppendAsync(newer, TestContext.Current.CancellationToken);
        await store.AppendAsync(Run("other-" + Guid.NewGuid(), _t0), TestContext.Current.CancellationToken);

        var rows = await store.ListByCommitAsync(commit, TestContext.Current.CancellationToken);

        Assert.Equal([newer.Id, older.Id], rows.Select(r => r.Id));
    }

    [Fact]
    public async Task Update_delete_and_truncate_are_rejected()
    {
        RequireDb();
        var run = Run("c-" + Guid.NewGuid(), _t0);
        await new PostgresCheckRunStore(db.DataSource).AppendAsync(run, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<PostgresException>(() => db.ExecAsync("UPDATE check_runs SET tests_failed = 0 WHERE id = $1", run.Id));
        await Assert.ThrowsAsync<PostgresException>(() => db.ExecAsync("DELETE FROM check_runs WHERE id = $1", run.Id));
        await Assert.ThrowsAsync<PostgresException>(() => db.ExecAsync("TRUNCATE check_runs"));
    }

    [Fact]
    public async Task Negative_counts_and_a_blank_commit_are_rejected()
    {
        RequireDb();
        var store = new PostgresCheckRunStore(db.DataSource);

        await Assert.ThrowsAsync<PostgresException>(async () =>
            await store.AppendAsync(Run("c-" + Guid.NewGuid(), _t0) with { TestsFailed = -1 }, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<PostgresException>(async () =>
            await store.AppendAsync(Run("  ", _t0), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Verdict_and_tool_exit_codes_round_trip()
    {
        RequireDb();
        var run = Run("c-" + Guid.NewGuid(), _t0) with
        {
            Verdict = CheckVerdict.Fail,
            ToolExitCodes = new Dictionary<string, int?> { ["build"] = 0, ["selene"] = 1, ["luau-analyze"] = null },
        };
        var store = new PostgresCheckRunStore(db.DataSource);
        await store.AppendAsync(run, TestContext.Current.CancellationToken);

        var back = await store.GetAsync(run.Id, TestContext.Current.CancellationToken);

        Assert.Equal(CheckVerdict.Fail, back!.Verdict);
        Assert.Equal(0, back.ToolExitCodes!["build"]);
        Assert.Equal(1, back.ToolExitCodes["selene"]);
        Assert.Null(back.ToolExitCodes["luau-analyze"]);
        Assert.True(back.ToolExitCodes.ContainsKey("luau-analyze"));
    }

    [Fact]
    public async Task An_unknown_verdict_is_refused_before_the_database()
    {
        var run = Run("c-x", _t0) with { Verdict = "maybe" };
        // The verdict check runs before any connection is opened, so this needs no database (it must also run where none exists).
        await using var dataSource = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=none");
        var store = new PostgresCheckRunStore(dataSource);

        await Assert.ThrowsAsync<ArgumentException>(async () => await store.AppendAsync(run, TestContext.Current.CancellationToken));
    }
}
