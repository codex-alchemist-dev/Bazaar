using Bazaar.Server.Playtest.Abstractions;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Playtest.Tests;

public sealed class PostgresPlaytestRunStoreTests
{
    private const string _skipReason = "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.";

    private static readonly DateTimeOffset _now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    private static PlaytestResult Sample(Guid? id = null, PlaytestStatus status = PlaytestStatus.AssertionsFailed) => new(
        id ?? Guid.NewGuid(),
        new PlaytestScenario(
            PlaytestEngine.Luau, "build-7", "menu-flow",
            [new PlaytestAssertion("a1", "score", "10"), new PlaytestAssertion("a2", "lives", "3")],
            TimeSpan.FromSeconds(30)),
        PlaytestMode.TierA,
        status,
        new StateDump(new Dictionary<string, string> { ["score"] = "10" }).ContentHash(),
        [new AssertionResult("a1", true, "10"), new AssertionResult("a2", false, "2"), new AssertionResult("a3", false, null)],
        "before-hash",
        "after-hash",
        _now);

    private static async Task MigrateAsync(NpgsqlDataSource dataSource, CancellationToken ct)
    {
        var assembly = typeof(PlaytestComponent).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.EndsWith(".sql", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
            await using var cmd = dataSource.CreateCommand(await reader.ReadToEndAsync(ct));
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task<long> CountAsync(NpgsqlDataSource dataSource, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("SELECT count(*) FROM playtest_run");
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static void AssertSame(PlaytestResult expected, PlaytestResult? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.RunId, actual.RunId);
        Assert.Equal(expected.Mode, actual.Mode);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.StateDumpHash, actual.StateDumpHash);
        Assert.Equal(expected.ProjectHashBefore, actual.ProjectHashBefore);
        Assert.Equal(expected.ProjectHashAfter, actual.ProjectHashAfter);
        Assert.Equal(expected.CreatedAt, actual.CreatedAt);
        Assert.Equal(expected.Scenario.Engine, actual.Scenario.Engine);
        Assert.Equal(expected.Scenario.BuildRef, actual.Scenario.BuildRef);
        Assert.Equal(expected.Scenario.ScenarioId, actual.Scenario.ScenarioId);
        Assert.Equal(expected.Scenario.Timeout, actual.Scenario.Timeout);
        Assert.Equal(expected.Scenario.Assertions, actual.Scenario.Assertions);
        Assert.Equal(expected.Assertions, actual.Assertions);
    }

    [Fact]
    public async Task Save_then_get_round_trips_a_run()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("playtest", MigrateAsync, ct);
        var store = new PostgresPlaytestRunStore(db.DataSource);
        var run = Sample();

        await store.SaveAsync(run, ct);

        AssertSame(run, await store.GetAsync(run.RunId, ct));
    }

    [Theory]
    [InlineData(PlaytestStatus.Passed)]
    [InlineData(PlaytestStatus.AssertionsFailed)]
    [InlineData(PlaytestStatus.ProjectChanged)]
    [InlineData(PlaytestStatus.TimedOut)]
    [InlineData(PlaytestStatus.HostFailed)]
    [InlineData(PlaytestStatus.ProfileRejected)]
    public async Task Every_status_survives_the_round_trip(PlaytestStatus status)
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("playtest", MigrateAsync, ct);
        var store = new PostgresPlaytestRunStore(db.DataSource);
        var run = Sample(status: status);

        await store.SaveAsync(run, ct);

        Assert.Equal(status, (await store.GetAsync(run.RunId, ct))!.Status);
    }

    [Fact]
    public async Task Null_hashes_and_empty_assertion_lists_round_trip_and_the_godot_engine_is_kept()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("playtest", MigrateAsync, ct);
        var store = new PostgresPlaytestRunStore(db.DataSource);
        var run = new PlaytestResult(
            Guid.NewGuid(),
            new PlaytestScenario(PlaytestEngine.Godot, "build-1", "boot", [], TimeSpan.FromMilliseconds(1500)),
            PlaytestMode.TierA, PlaytestStatus.HostFailed, null, [], null, null, _now);

        await store.SaveAsync(run, ct);
        var loaded = await store.GetAsync(run.RunId, ct);

        Assert.NotNull(loaded);
        Assert.Equal(PlaytestEngine.Godot, loaded.Scenario.Engine);
        Assert.Null(loaded.StateDumpHash);
        Assert.Null(loaded.ProjectHashBefore);
        Assert.Null(loaded.ProjectHashAfter);
        Assert.Empty(loaded.Scenario.Assertions);
        Assert.Empty(loaded.Assertions);
        // The column holds whole seconds, so the timeout rounds up.
        Assert.Equal(TimeSpan.FromSeconds(2), loaded.Scenario.Timeout);
    }

    [Fact]
    public async Task Get_returns_null_for_an_unknown_run()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("playtest", MigrateAsync, ct);
        var store = new PostgresPlaytestRunStore(db.DataSource);
        await store.SaveAsync(Sample(), ct);

        Assert.Null(await store.GetAsync(Guid.NewGuid(), ct));
    }

    [Fact]
    public async Task Runs_are_stored_independently_and_saving_the_same_id_twice_is_rejected_without_changing_the_row()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("playtest", MigrateAsync, ct);
        var store = new PostgresPlaytestRunStore(db.DataSource);
        var first = Sample();
        var second = Sample(status: PlaytestStatus.Passed);
        await store.SaveAsync(first, ct);
        await store.SaveAsync(second, ct);

        // The store has no update or list: SaveAsync is an insert, so a repeat of an id is a primary-key violation.
        await Assert.ThrowsAsync<PostgresException>(() => store.SaveAsync(first with { Status = PlaytestStatus.Passed }, ct));

        AssertSame(first, await store.GetAsync(first.RunId, ct));
        AssertSame(second, await store.GetAsync(second.RunId, ct));
        Assert.Equal(2, await CountAsync(db.DataSource, ct));
    }

    [Fact]
    public async Task Tier_a_rows_carry_a_zero_reputation_weight_and_empty_tier_b_columns()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("playtest", MigrateAsync, ct);
        var store = new PostgresPlaytestRunStore(db.DataSource);
        var run = Sample();
        await store.SaveAsync(run, ct);

        await using var cmd = db.DataSource.CreateCommand(
            "SELECT mode, reputation_weight, input_log_ref, snapshot_blob_refs::text FROM playtest_run WHERE id = $1");
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = run.RunId });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        Assert.True(await reader.ReadAsync(ct));
        Assert.Equal("tier_a", reader.GetString(0));
        Assert.Equal(PlaytestResult.ReputationWeight, reader.GetInt32(1));
        Assert.True(reader.IsDBNull(2));
        Assert.Equal("[]", reader.GetString(3));
    }

    [Fact]
    public async Task Save_rejects_a_null_result()
    {
        var store = new PostgresPlaytestRunStore(null!);

        await Assert.ThrowsAsync<ArgumentNullException>(() => store.SaveAsync(null!, TestContext.Current.CancellationToken));
    }
}
