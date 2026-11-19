using Bazaar.Server.Data.Migrations;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Tests.Data.Migrations;

/// <summary>
/// Fault tests for the migration runner (doc 04, doc 46 section 17.1): a backend killed mid-run, a locked history table, a damaged history
/// table, and the refusal path of the <c>migrate</c> command. Chaos campaign "migrations".
/// </summary>
public sealed class MigrationFaultTests(MigrationDb db) : IClassFixture<MigrationDb>
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

    private static async Task ExecAsync(NpgsqlDataSource ds, string sql)
    {
        await using var cmd = ds.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_backend_killed_mid_migration_leaves_neither_ddl_nor_history_and_a_rerun_converges()
    {
        var ds = await FreshAsync(db);
        var killer = Common(2, "two", "CREATE TABLE t_two (id int); SELECT pg_terminate_backend(pg_backend_pid()); SELECT pg_sleep(5)");
        var one = Common(1, "one", "CREATE TABLE t_one (id int)");

        await Assert.ThrowsAsync<MigrationException>(() => new MigrationRunner(ds).MigrateAsync(Source(one, killer), "linux", TestContext.Current.CancellationToken));

        Assert.Equal(1, await ScalarAsync(ds, "SELECT count(*) FROM schema_migrations"));
        Assert.Equal(0, await ScalarAsync(ds, "SELECT count(*) FROM pg_tables WHERE tablename = 't_two'"));

        var fixedTwo = Common(2, "two", "CREATE TABLE t_two (id int)");
        var rerun = await new MigrationRunner(ds).MigrateAsync(Source(one, fixedTwo), "linux", TestContext.Current.CancellationToken);
        Assert.Equal([2], rerun.Applied.Select(a => a.Version));
        Assert.Equal(2, await ScalarAsync(ds, "SELECT count(*) FROM schema_migrations"));
    }

    [Fact]
    public async Task A_locked_history_table_makes_migrate_refuse_after_the_lock_timeout_instead_of_waiting_forever()
    {
        var ds = await FreshAsync(db);
        var files = Source(Common(1, "one", "CREATE TABLE t_one (id int)"));
        await new MigrationRunner(ds).MigrateAsync(files, "linux", TestContext.Current.CancellationToken);

        await using var holder = await ds.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var tx = await holder.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (var lockCmd = new NpgsqlCommand("LOCK TABLE schema_migrations IN ACCESS EXCLUSIVE MODE", holder, tx))
            await lockCmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        var runner = new MigrationRunner(ds, new MigrationOptions(LockTimeout: TimeSpan.FromMilliseconds(400)));
        var started = DateTime.UtcNow;
        var ex = await Assert.ThrowsAsync<MigrationException>(() => runner.MigrateAsync(files, "linux", TestContext.Current.CancellationToken));

        Assert.Contains("waiting for a database lock", ex.Message, StringComparison.Ordinal);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(20));
        await tx.RollbackAsync(TestContext.Current.CancellationToken);

        // The failed run released its advisory lock and reset the session: the next run works.
        var after = await new MigrationRunner(ds).MigrateAsync(files, "linux", TestContext.Current.CancellationToken);
        Assert.Equal(1, after.AlreadyApplied);
    }

    [Fact]
    public async Task The_startup_gate_refuses_a_recorded_checksum_that_no_longer_matches()
    {
        var ds = await FreshAsync(db);
        var files = Source(Common(1, "one", "CREATE TABLE t_one (id int)"));
        var runner = new MigrationRunner(ds);
        await runner.MigrateAsync(files, "linux", TestContext.Current.CancellationToken);
        await ExecAsync(ds, "UPDATE schema_migrations SET checksum = 'deadbeefdeadbeefdeadbeef'");

        await Assert.ThrowsAsync<MigrationChecksumException>(() => runner.EnsureCurrentAsync(files, "linux", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_history_table_with_the_wrong_column_types_is_refused_with_a_migration_exception()
    {
        var ds = await FreshAsync(db);
        await ExecAsync(ds, "CREATE TABLE schema_migrations (provider int, version text, junk jsonb)");
        var files = Source(Common(1, "one", "CREATE TABLE t_one (id int)"));

        var ex = await Assert.ThrowsAsync<SchemaVersionException>(() => new MigrationRunner(ds).MigrateAsync(files, "linux", TestContext.Current.CancellationToken));
        Assert.Contains("schema_migrations", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, await ScalarAsync(ds, "SELECT count(*) FROM pg_tables WHERE tablename = 't_one'"));

        await ExecAsync(ds, "DROP TABLE schema_migrations; CREATE TABLE schema_migrations (provider text, namespace text NOT NULL DEFAULT 'common', version text, name text, checksum text, applied_at timestamptz)");
        await Assert.ThrowsAsync<SchemaVersionException>(() => new MigrationRunner(ds).EnsureCurrentAsync(files, "linux", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Migrate_against_an_unreachable_database_writes_one_line_and_no_secret()
    {
        var output = new StringWriter();
        var code = await MigrateCommand.RunAsync(
            "Host=127.0.0.1;Port=1;Database=x;Username=u;Password=hunter2-secret;Timeout=2",
            Source(Common(1, "one", "CREATE TABLE t_one (id int)")),
            "linux",
            output,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, code);
        var text = output.ToString().TrimEnd();
        Assert.DoesNotContain('\n', text);
        Assert.StartsWith("migrate refused:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Migrate_with_a_malformed_connection_string_writes_one_line_and_no_secret()
    {
        var output = new StringWriter();
        var code = await MigrateCommand.RunAsync(
            "Host=x;Password=hunter2-secret;Bogus=1",
            Source(Common(1, "one", "CREATE TABLE t_one (id int)")),
            "linux",
            output,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, code);
        Assert.DoesNotContain("hunter2", output.ToString(), StringComparison.Ordinal);
        Assert.StartsWith("migrate refused:", output.ToString(), StringComparison.Ordinal);
    }
}
