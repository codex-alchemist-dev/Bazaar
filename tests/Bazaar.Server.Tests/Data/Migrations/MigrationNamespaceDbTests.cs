using Bazaar.Server.Components;
using Bazaar.Server.Data.Migrations;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Tests.Data.Migrations;

/// <summary>
/// Namespace-keyed history against a real PostgreSQL (components design section 5). Needs Docker or <c>BAZAAR_TEST_PG</c>;
/// skipped where no test PostgreSQL is available, like the other database suites.
/// </summary>
public sealed class MigrationNamespaceDbTests(MigrationDb db) : IClassFixture<MigrationDb>
{
    private static ComponentManifest Component(string id) => new(id, "0.1.0", ComponentKind.Feature, [], []);

    private static MigrationFile Mig(string ns, int version, string name, string sql) => new(ns, version, name, sql);

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

    private static async Task<string> TextAsync(NpgsqlDataSource ds, string sql)
    {
        await using var cmd = ds.CreateCommand(sql);
        return (string)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    [Fact]
    public async Task Namespaces_apply_in_plan_order_and_the_same_version_can_repeat_across_namespaces()
    {
        var ds = await FreshAsync(db);
        var ct = TestContext.Current.CancellationToken;
        var source = new InMemoryMigrationSource(
        [
            Mig("common", 1, "base", "CREATE TABLE base_t (id int)"),
            Mig("tasks", 1, "tasks", "CREATE TABLE tasks_t (id int)"),
            Mig("tasks", 2, "more", "CREATE INDEX tasks_t_idx ON tasks_t (id)"),
            Mig("notes", 1, "notes", "CREATE TABLE notes_t (id int)"),
        ]);

        var result = await new MigrationRunner(ds, components: [Component("tasks")]).MigrateAsync(source, "linux", ct);

        Assert.Equal(["common", "tasks", "tasks", "notes"], result.Applied.Select(a => a.Namespace));
        Assert.Equal(4, await ScalarAsync(ds, "SELECT count(*) FROM schema_migrations"));
        Assert.Equal(1, await ScalarAsync(ds, "SELECT count(*) FROM schema_migrations WHERE namespace = 'tasks' AND version = 1"));
        Assert.Equal(1, await ScalarAsync(ds, "SELECT count(*) FROM schema_migrations WHERE namespace = 'common' AND version = 1"));
    }

    [Fact]
    public async Task Each_file_records_its_namespace_and_the_primary_key_is_namespace_and_version()
    {
        var ds = await FreshAsync(db);
        var ct = TestContext.Current.CancellationToken;
        var source = new InMemoryMigrationSource([Mig("common", 1, "a", "SELECT 1"), Mig("tasks", 1, "b", "SELECT 1")]);

        await new MigrationRunner(ds).MigrateAsync(source, "linux", ct);

        Assert.Equal("common", await TextAsync(ds, "SELECT provider FROM schema_migrations WHERE namespace = 'common'"));
        Assert.Equal("tasks", await TextAsync(ds, "SELECT provider FROM schema_migrations WHERE namespace = 'tasks'"));
        Assert.Equal(
            "PRIMARY KEY (namespace, version)",
            await TextAsync(ds, "SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conrelid = 'schema_migrations'::regclass AND contype = 'p'"));
    }

    [Fact]
    public async Task A_history_table_from_before_namespaces_is_upgraded_and_its_rows_stay_valid()
    {
        var ds = await FreshAsync(db);
        var ct = TestContext.Current.CancellationToken;
        var core = Mig("common", 1, "core", "CREATE TABLE core_t (id int)");
        var gate = Mig("common", 2, "gate", "CREATE TABLE gate_t (id int)");

        // The shape the runner created before namespaces: keyed by (provider, version), no namespace column.
        var legacy = $"""
            CREATE TABLE schema_migrations (
                provider text NOT NULL, version integer NOT NULL, name text NOT NULL, checksum text NOT NULL,
                applied_at timestamptz NOT NULL DEFAULT now(), PRIMARY KEY (provider, version));
            CREATE TABLE core_t (id int);
            INSERT INTO schema_migrations (provider, version, name, checksum) VALUES ('common', 1, 'core', '{core.Checksum}');
            """;
        await using (var cmd = ds.CreateCommand(legacy))
            await cmd.ExecuteNonQueryAsync(ct);

        var source = new InMemoryMigrationSource([core, gate]);
        var runner = new MigrationRunner(ds);

        // Startup never upgrades; it reports the table as behind.
        Assert.Equal(SchemaState.Behind, await runner.GetStateAsync(source, "linux", ct));
        Assert.Equal(0, await ScalarAsync(ds, "SELECT count(*) FROM information_schema.columns WHERE table_name = 'schema_migrations' AND column_name = 'namespace'"));

        var result = await runner.MigrateAsync(source, "linux", ct);

        Assert.Equal([2], result.Applied.Select(a => a.Version));
        Assert.Equal(1, result.AlreadyApplied);
        Assert.Equal("common", await TextAsync(ds, "SELECT namespace FROM schema_migrations WHERE version = 1"));
        Assert.Equal(SchemaState.Current, await runner.GetStateAsync(source, "linux", ct));
    }

    [Fact]
    public async Task Upgraded_provider_rows_keep_their_folder_as_namespace()
    {
        var ds = await FreshAsync(db);
        var ct = TestContext.Current.CancellationToken;
        var idx = Mig("linux", 1, "idx", "SELECT 1");

        var legacy = $"""
            CREATE TABLE schema_migrations (
                provider text NOT NULL, version integer NOT NULL, name text NOT NULL, checksum text NOT NULL,
                applied_at timestamptz NOT NULL DEFAULT now(), PRIMARY KEY (provider, version));
            INSERT INTO schema_migrations (provider, version, name, checksum) VALUES ('linux', 1, 'idx', '{idx.Checksum}');
            """;
        await using (var cmd = ds.CreateCommand(legacy))
            await cmd.ExecuteNonQueryAsync(ct);

        await new MigrationRunner(ds).MigrateAsync(new InMemoryMigrationSource([idx]), "linux", ct);

        Assert.Equal("linux", await TextAsync(ds, "SELECT namespace FROM schema_migrations WHERE provider = 'linux'"));
    }

    [Fact]
    public async Task The_history_upgrade_is_idempotent()
    {
        var ds = await FreshAsync(db);
        var ct = TestContext.Current.CancellationToken;
        var source = new InMemoryMigrationSource([Mig("common", 1, "a", "SELECT 1")]);
        var runner = new MigrationRunner(ds);

        await runner.MigrateAsync(source, "linux", ct);
        var again = await runner.MigrateAsync(source, "linux", ct);

        Assert.Empty(again.Applied);
        Assert.Equal(1, await ScalarAsync(ds, "SELECT count(*) FROM schema_migrations"));
    }

    [Fact]
    public async Task A_namespaced_database_refuses_an_older_build_that_lacks_the_namespace()
    {
        var ds = await FreshAsync(db);
        var ct = TestContext.Current.CancellationToken;
        var source = new InMemoryMigrationSource([Mig("common", 1, "a", "SELECT 1"), Mig("tasks", 1, "b", "SELECT 1")]);
        await new MigrationRunner(ds).MigrateAsync(source, "linux", ct);

        var olderBuild = new InMemoryMigrationSource([Mig("common", 1, "a", "SELECT 1")]);
        Assert.Equal(SchemaState.Ahead, await new MigrationRunner(ds).GetStateAsync(olderBuild, "linux", ct));
    }
}
