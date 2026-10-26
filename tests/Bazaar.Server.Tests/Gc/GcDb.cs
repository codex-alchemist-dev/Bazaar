using Bazaar.Server.Data.Migrations;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Tests.Gc;

/// <summary>A cloned test database migrated with the real core schema (<c>common/0001</c>), plus helpers to create GCs and read raw state.</summary>
public sealed class GcDb : PgFixture, IAsyncLifetime
{
    private NpgsqlDataSource? _pool;

    /// <summary>Pooled data source for this class's database, capped at 12 connections.</summary>
    public new NpgsqlDataSource DataSource => _pool ?? throw new InvalidOperationException("GcDb is not initialised.");

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

    public async Task<Guid> CreateGcAsync(bool archived = false)
    {
        var id = Guid.CreateVersion7();
        await using var cmd = DataSource.CreateCommand("INSERT INTO gc (gc_id, type_key, title, created_by, archived_at) VALUES ($1, 'general', 'test', $1, CASE WHEN $2 THEN now() END)");
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
        cmd.Parameters.Add(new NpgsqlParameter<bool> { TypedValue = archived });
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    public async Task<T> ScalarAsync<T>(string sql, params object[] args)
    {
        await using var cmd = DataSource.CreateCommand(sql);
        foreach (var a in args)
            cmd.Parameters.Add(new NpgsqlParameter { Value = a });
        return (T)(await cmd.ExecuteScalarAsync())!;
    }
}

internal static class GcTestSkip
{
    // Windows without BAZAAR_TEST_PG has no database by design (test.ci.windows_db_job = false); Linux never skips.
    public static void WithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
}
