using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Tests.Data;

public sealed class DbA : PgFixture, IAsyncLifetime
{
    public override ValueTask InitializeAsync() => PgSource.IsUnsupportedHost ? ValueTask.CompletedTask : base.InitializeAsync();
}

public sealed class DbB : PgFixture, IAsyncLifetime
{
    public override ValueTask InitializeAsync() => PgSource.IsUnsupportedHost ? ValueTask.CompletedTask : base.InitializeAsync();
}

public static class PgFixtureTemplate
{
    // Stand-in for the real migration runner (M1): proves the template is migrated once and cloned.
    public static int MigrationRuns => _migrationRuns;
    private static int _migrationRuns;

#pragma warning disable CA2255 // the template must be registered before any fixture initialises
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Register() => PgTemplate.UseMigrator(async cs =>
    {
        Interlocked.Increment(ref _migrationRuns);
        // Migration 0001 creates the cluster-wide bazaar_app role with a check-then-create, which races when test classes
        // migrate databases in parallel. Creating it once here, before any class runs, removes the race.
        await using var ds = NpgsqlDataSource.Create(cs);
        await PgRoles.EnsureNoLoginRoleAsync(ds, "bazaar_app", TestContext.Current.CancellationToken);
        await using var cmd = ds.CreateCommand("CREATE TABLE template_marker(id int); INSERT INTO template_marker VALUES (1)");
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    });
#pragma warning restore CA2255
}

/// <summary>Self-test of the Postgres fixture against a real server.</summary>
public sealed class PgFixtureTests(DbA a, DbB b) : IClassFixture<DbA>, IClassFixture<DbB>
{
    private static async Task<long> ScalarAsync(NpgsqlDataSource ds, string sql)
    {
        await using var cmd = ds.CreateCommand(sql);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    // Windows without BAZAAR_TEST_PG has no database yet by design (test.ci.windows_db_job = false); Linux never skips.
    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    [Fact]
    public async Task Each_class_gets_its_own_database()
    {
        SkipWithoutDatabase();
        Assert.NotEqual(a.DatabaseName, b.DatabaseName);
        await using (var cmd = a.DataSource.CreateCommand("CREATE TABLE only_in_a(id int)"))
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, await ScalarAsync(a.DataSource, "SELECT count(*) FROM pg_tables WHERE tablename = 'only_in_a'"));
        Assert.Equal(0, await ScalarAsync(b.DataSource, "SELECT count(*) FROM pg_tables WHERE tablename = 'only_in_a'"));
    }

    [Fact]
    public async Task Clone_carries_the_migrated_template_content()
    {
        SkipWithoutDatabase();
        Assert.Equal(1, await ScalarAsync(a.DataSource, "SELECT count(*) FROM template_marker"));
        Assert.Equal(1, await ScalarAsync(b.DataSource, "SELECT count(*) FROM template_marker"));
        Assert.Equal(1, PgFixtureTemplate.MigrationRuns);
    }

    [Fact]
    public async Task Server_is_postgres_18_with_pgvector_available()
    {
        SkipWithoutDatabase();
        Assert.Equal(18, await ScalarAsync(a.DataSource, "SELECT current_setting('server_version_num')::int / 10000"));
        Assert.Equal(1, await ScalarAsync(a.DataSource, "SELECT count(*) FROM pg_available_extensions WHERE name = 'vector'"));
    }

    [Fact]
    public async Task Notify_round_trips()
    {
        SkipWithoutDatabase();
        await using var listener = await a.OpenConnectionAsync(TestContext.Current.CancellationToken);
        var got = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.Notification += (_, e) => got.TrySetResult(e.Payload);
        await using (var listen = new NpgsqlCommand("LISTEN t3_selftest", listener))
            await listen.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        await using (var notify = a.DataSource.CreateCommand("NOTIFY t3_selftest, 'hi'"))
            await notify.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        await using (var wait = new NpgsqlCommand("SELECT 1", listener))
            await wait.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        Assert.Equal("hi", await got.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }
}
