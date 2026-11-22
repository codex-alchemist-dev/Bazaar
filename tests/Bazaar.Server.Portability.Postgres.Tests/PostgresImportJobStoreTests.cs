using Bazaar.Server.Data.Migrations;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Portability.Postgres.Tests;

/// <summary>
/// Runs the store contract against PostgreSQL. The server comes from <c>BAZAAR_TEST_PG</c>, or a Testcontainers container when
/// it is unset (<see cref="PgSource"/>). Each test gets its own database, migrated with the portability namespace only.
/// </summary>
public sealed class PostgresImportJobStoreTests : ImportJobStoreContract
{
    protected override async Task<StoreHandle> OpenAsync(CancellationToken ct)
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var db = await PgTestDatabase.CreateAsync("import", MigrateAsync, ct);
        return new StoreHandle(new PostgresImportJobStore(db.DataSource), () => db.DisposeAsync());
    }

    internal static async Task MigrateAsync(NpgsqlDataSource dataSource, CancellationToken ct) =>
        await new MigrationRunner(dataSource).MigrateAsync(
            new EmbeddedMigrationSource(typeof(PostgresImportJobStore).Assembly), MigrateCommand.CurrentProvider(), ct);
}

/// <summary>Checks the schema itself: the migration can run again, and the table rules refuse what the store must never write.</summary>
public sealed class PostgresImportJobSchemaTests
{
    [Fact]
    public async Task Migration_reruns_as_a_no_op()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("import", PostgresImportJobStoreTests.MigrateAsync, ct);

        await PostgresImportJobStoreTests.MigrateAsync(db.DataSource, ct);

        await using var cmd = db.DataSource.CreateCommand(
            "SELECT count(*) FROM schema_migrations WHERE namespace = 'portability' AND version = 1 AND name = 'import_job'");
        Assert.Equal(1L, await cmd.ExecuteScalarAsync(ct));
    }

    [Fact]
    public async Task Terminal_state_without_an_end_time_is_refused()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("import", PostgresImportJobStoreTests.MigrateAsync, ct);

        await Assert.ThrowsAsync<PostgresException>(async () => await InsertJobAsync(db.DataSource, "failed", endedAt: false, ct));
    }

    [Fact]
    public async Task Unknown_state_is_refused()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("import", PostgresImportJobStoreTests.MigrateAsync, ct);

        await Assert.ThrowsAsync<PostgresException>(async () => await InsertJobAsync(db.DataSource, "bogus", endedAt: false, ct));
    }

    [Fact]
    public async Task Terminal_state_with_an_end_time_is_accepted()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("import", PostgresImportJobStoreTests.MigrateAsync, ct);

        await InsertJobAsync(db.DataSource, "cancelled", endedAt: true, ct);
    }

    private static async Task InsertJobAsync(NpgsqlDataSource dataSource, string state, bool endedAt, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(
            "INSERT INTO import_job (job_id, actor_id, importer_id, importer_version, state, max_upload_bytes, source_manifest, created_at, ended_at) " +
            "VALUES ('job-x', 'owner', 'chat-dump', '1.0.0', $1, 10, '[]'::jsonb, now(), $2)");
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = state });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset?> { TypedValue = endedAt ? DateTimeOffset.UtcNow : null });
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
