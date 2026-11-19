using Bazaar.Server.Data.Migrations;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Modules.Tests;

/// <summary>
/// A cloned test database migrated with the real core schema (<c>common/0001</c>), for the module manager's persistence tests.
/// The same fixture shape as the Bazaar.Server.Tests <c>GcDb</c>, kept local so this project does not reference that test assembly.
/// </summary>
public sealed class ModuleDb : PgFixture, IAsyncLifetime
{
    private NpgsqlDataSource? _pool;

    /// <summary>Pooled data source for this class's database, capped at 12 connections.</summary>
    public new NpgsqlDataSource DataSource => _pool ?? throw new InvalidOperationException("ModuleDb is not initialised.");

    public override async ValueTask InitializeAsync()
    {
        if (PgSource.IsUnsupportedHost)
            return;
        await base.InitializeAsync();
        // A small pool per class: the CI server allows 100 connections in total and test classes run in parallel.
        _pool = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(ConnectionString) { MaxPoolSize = 12 }.ConnectionString);
        await new MigrationRunner(DataSource, new MigrationOptions(16))
            .MigrateAsync(new EmbeddedMigrationSource(typeof(MigrationRunner).Assembly), "linux", CancellationToken.None);
    }

    /// <summary>Runs a scalar query on the fixture's pool and returns its value as <typeparamref name="T"/>.</summary>
    public async Task<T> ScalarAsync<T>(string sql, params object[] args)
    {
        await using var cmd = DataSource.CreateCommand(sql);
        foreach (var a in args)
            cmd.Parameters.Add(new NpgsqlParameter { Value = a });
        return (T)(await cmd.ExecuteScalarAsync())!;
    }
}

internal static class ModuleTestSkip
{
    // Windows without BAZAAR_TEST_PG has no database by design (test.ci.windows_db_job = false); Linux never skips.
    public static void WithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
}
