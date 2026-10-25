using Bazaar.Server.Data.Migrations;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Tests.Data.Migrations;

public sealed class MigrationDb : PgFixture, IAsyncLifetime
{
    public override ValueTask InitializeAsync() => PgSource.IsUnsupportedHost ? ValueTask.CompletedTask : base.InitializeAsync();
}

/// <summary>Migration runner against a real PostgreSQL (doc 04, doc 34).</summary>
public sealed class MigrationRunnerTests(MigrationDb db) : IClassFixture<MigrationDb>
{
    private static MigrationFile Common(int v, string name, string sql) => new("common", v, name, sql);

    private static InMemoryMigrationSource Source(params MigrationFile[] files) => new(files);

    private static async Task<NpgsqlDataSource> FreshAsync(MigrationDb db)
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        await using (var cmd = db.DataSource.CreateCommand("DROP SCHEMA public CASCADE; CREATE SCHEMA public"))
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return db.DataSource;
    }

    private static async Task<long> ScalarAsync(NpgsqlDataSource ds, string sql)
    {
        await using var cmd = ds.CreateCommand(sql);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static readonly MigrationFile[] Basic =
    [
        Common(1, "one", "CREATE TABLE t_one (id int)"),
        Common(2, "two", "CREATE TABLE t_two (id int); INSERT INTO t_two VALUES (7)"),
    ];

    [Fact]
    public async Task Fresh_database_applies_every_file_in_order()
    {
        var ds = await FreshAsync(db);
        var result = await new MigrationRunner(ds).MigrateAsync(Source(Basic), "linux", TestContext.Current.CancellationToken);

        Assert.Equal([1, 2], result.Applied.Select(a => a.Version));
        Assert.Equal(0, result.AlreadyApplied);
        Assert.Equal(7, await ScalarAsync(ds, "SELECT id FROM t_two"));
        Assert.Equal(2, await ScalarAsync(ds, "SELECT count(*) FROM schema_migrations"));
    }

    [Fact]
    public async Task Reapplying_is_a_no_op()
    {
        var ds = await FreshAsync(db);
        var runner = new MigrationRunner(ds);
        await runner.MigrateAsync(Source(Basic), "linux", TestContext.Current.CancellationToken);
        var again = await runner.MigrateAsync(Source(Basic), "linux", TestContext.Current.CancellationToken);

        Assert.Empty(again.Applied);
        Assert.Equal(2, again.AlreadyApplied);
        Assert.Equal(2, await ScalarAsync(ds, "SELECT count(*) FROM schema_migrations"));
    }

    [Fact]
    public async Task Edited_applied_file_is_rejected()
    {
        var ds = await FreshAsync(db);
        var runner = new MigrationRunner(ds);
        await runner.MigrateAsync(Source(Basic), "linux", TestContext.Current.CancellationToken);

        var tampered = Source(Common(1, "one", "CREATE TABLE t_one (id int, extra int)"), Basic[1]);
        var ex = await Assert.ThrowsAsync<MigrationChecksumException>(
            () => runner.MigrateAsync(tampered, "linux", TestContext.Current.CancellationToken));
        Assert.Contains("0001_one", ex.Message);
    }

    [Fact]
    public async Task Line_endings_do_not_change_the_checksum()
    {
        var lf = Common(1, "one", "CREATE TABLE t_one (\n id int\n)");
        var crlf = Common(1, "one", "CREATE TABLE t_one (\r\n id int\r\n)");
        Assert.Equal(lf.Checksum, crlf.Checksum);

        var ds = await FreshAsync(db);
        var runner = new MigrationRunner(ds);
        await runner.MigrateAsync(Source(lf), "linux", TestContext.Current.CancellationToken);
        var again = await runner.MigrateAsync(Source(crlf), "linux", TestContext.Current.CancellationToken);
        Assert.Empty(again.Applied);
    }

    [Fact]
    public async Task Two_runners_racing_apply_each_file_once()
    {
        var ds = await FreshAsync(db);
        var files = Enumerable.Range(1, 6).Select(i => Common(i, $"t{i}", $"CREATE TABLE race_{i} (id int); SELECT pg_sleep(0.05)")).ToArray();

        var results = await Task.WhenAll(
            Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
                new MigrationRunner(ds).MigrateAsync(Source(files), "linux", TestContext.Current.CancellationToken), TestContext.Current.CancellationToken)));

        Assert.Equal(6, results.Sum(r => r.Applied.Count));
        Assert.Equal(6, await ScalarAsync(ds, "SELECT count(*) FROM schema_migrations"));
    }

    [Fact]
    public async Task Failing_file_rolls_back_and_later_files_do_not_run()
    {
        var ds = await FreshAsync(db);
        var runner = new MigrationRunner(ds);
        var source = Source(
            Common(1, "ok", "CREATE TABLE ok_t (id int)"),
            Common(2, "bad", "CREATE TABLE half_t (id int); SELECT 1/0"),
            Common(3, "later", "CREATE TABLE later_t (id int)"));

        var ex = await Assert.ThrowsAsync<MigrationException>(() => runner.MigrateAsync(source, "linux", TestContext.Current.CancellationToken));
        Assert.Contains("0002_bad", ex.Message);
        Assert.Equal(1, await ScalarAsync(ds, "SELECT count(*) FROM schema_migrations"));
        Assert.Equal(0, await ScalarAsync(ds, "SELECT count(*) FROM pg_tables WHERE tablename IN ('half_t', 'later_t')"));

        // The lock was released: a corrected run continues from the last good version.
        var fixedSource = Source(source.Load("common")[0], Common(2, "bad", "CREATE TABLE half_t (id int)"));
        var retry = await runner.MigrateAsync(fixedSource, "linux", TestContext.Current.CancellationToken);
        Assert.Single(retry.Applied);
    }

    [Fact]
    public async Task Provider_folder_runs_after_common_and_only_for_its_provider()
    {
        var ds = await FreshAsync(db);
        var source = Source(
            Common(1, "base", "CREATE TABLE base_t (id int)"),
            new MigrationFile("linux", 1, "idx", "CREATE INDEX base_t_idx ON base_t (id)"),
            new MigrationFile("windows", 1, "other", "CREATE INDEX base_t_win ON base_t (id)"));

        var result = await new MigrationRunner(ds).MigrateAsync(source, "linux", TestContext.Current.CancellationToken);

        Assert.Equal(["common", "linux"], result.Applied.Select(a => a.Provider));
        Assert.Equal(1, await ScalarAsync(ds, "SELECT count(*) FROM pg_indexes WHERE indexname = 'base_t_idx'"));
        Assert.Equal(0, await ScalarAsync(ds, "SELECT count(*) FROM pg_indexes WHERE indexname = 'base_t_win'"));
    }

    [Fact]
    public async Task No_transaction_file_can_create_an_index_concurrently()
    {
        var ds = await FreshAsync(db);
        var source = Source(
            Common(1, "t", "CREATE TABLE c_t (id int)"),
            Common(2, "idx", "-- @no-transaction\nCREATE INDEX CONCURRENTLY c_t_idx ON c_t (id)"));

        await new MigrationRunner(ds).MigrateAsync(source, "linux", TestContext.Current.CancellationToken);

        Assert.Equal(1, await ScalarAsync(ds, "SELECT count(*) FROM pg_indexes WHERE indexname = 'c_t_idx'"));
        Assert.Equal(2, await ScalarAsync(ds, "SELECT count(*) FROM schema_migrations"));
    }

    [Fact]
    public async Task Database_with_an_unknown_migration_is_refused_as_newer()
    {
        var ds = await FreshAsync(db);
        var runner = new MigrationRunner(ds);
        await runner.MigrateAsync(Source(Basic), "linux", TestContext.Current.CancellationToken);

        var older = Source(Basic[0]);
        await Assert.ThrowsAsync<SchemaVersionException>(() => runner.MigrateAsync(older, "linux", TestContext.Current.CancellationToken));
        Assert.Equal(SchemaState.Ahead, await runner.GetStateAsync(older, "linux", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_new_file_numbered_below_an_applied_one_is_refused()
    {
        var ds = await FreshAsync(db);
        var runner = new MigrationRunner(ds);
        await runner.MigrateAsync(Source(Common(1, "a", "SELECT 1"), Common(3, "c", "SELECT 1")), "linux", TestContext.Current.CancellationToken);

        var withGap = Source(Common(1, "a", "SELECT 1"), Common(2, "b", "SELECT 1"), Common(3, "c", "SELECT 1"));
        await Assert.ThrowsAsync<SchemaVersionException>(() => runner.MigrateAsync(withGap, "linux", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Duplicate_version_in_one_folder_is_refused()
    {
        var ds = await FreshAsync(db);
        var source = Source(Common(1, "a", "SELECT 1"), Common(1, "b", "SELECT 1"));
        await Assert.ThrowsAsync<MigrationException>(() => new MigrationRunner(ds).MigrateAsync(source, "linux", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Startup_check_reports_behind_current_and_refuses_otherwise()
    {
        var ds = await FreshAsync(db);
        var runner = new MigrationRunner(ds);
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(SchemaState.Behind, await runner.GetStateAsync(Source(Basic), "linux", ct));
        await Assert.ThrowsAsync<SchemaVersionException>(() => runner.EnsureCurrentAsync(Source(Basic), "linux", ct));

        await runner.MigrateAsync(Source(Basic), "linux", ct);
        await runner.EnsureCurrentAsync(Source(Basic), "linux", ct);
        Assert.Equal(SchemaState.Current, await runner.GetStateAsync(Source(Basic), "linux", ct));

        var newer = Source(Basic[0], Basic[1], Common(3, "three", "SELECT 1"));
        Assert.Equal(SchemaState.Behind, await runner.GetStateAsync(newer, "linux", ct));
        Assert.Equal(2, await ScalarAsync(ds, "SELECT count(*) FROM schema_migrations"));
    }

    [Fact]
    public async Task Migrate_command_returns_zero_on_success_and_one_on_refusal()
    {
        await FreshAsync(db);
        var output = new StringWriter();
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(0, await MigrateCommand.RunAsync(db.ConnectionString, Source(Basic), "linux", output, ct));
        Assert.Contains("done: 2 applied", output.ToString(), StringComparison.Ordinal);

        var tampered = Source(Common(1, "one", "CREATE TABLE t_one (id bigint)"), Basic[1]);
        Assert.Equal(1, await MigrateCommand.RunAsync(db.ConnectionString, tampered, "linux", output, ct));
        Assert.Contains("migrate refused", output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0001_core.sql", true, 1, "core")]
    [InlineData("0042_gateway_tables.sql", true, 42, "gateway_tables")]
    [InlineData("1_core.sql", false, 0, "")]
    [InlineData("0001_.sql", false, 0, "")]
    [InlineData("0001_core.txt", false, 0, "")]
    public void File_names_follow_the_NNNN_name_convention(string file, bool ok, int version, string name)
    {
        Assert.Equal(ok, MigrationFile.TryParseFileName(file, out var v, out var n));
        Assert.Equal(version, v);
        Assert.Equal(name, n);
    }
}
