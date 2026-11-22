using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Portability;
using Npgsql;

namespace Bazaar.Server.Portability.Postgres.Tests;

/// <summary>
/// The portability migration as embedded in the assembly: it is found under its own namespace, numbered from 1, and creates
/// the three tables. No database is needed for these checks.
/// </summary>
public class ImportMigrationTests
{
    private static readonly EmbeddedMigrationSource _source = new(typeof(PostgresImportJobStore).Assembly);

    private static string Sql => _source.Load("portability").Single(f => f.Version == 1).Sql;

    [Fact]
    public void Portability_namespace_holds_migration_0001_import_job()
    {
        Assert.Contains("portability", _source.Namespaces());
        var file = Assert.Single(_source.Load("portability"), f => f.Version == 1);

        Assert.Equal("portability", file.Namespace);
        Assert.Equal(1, file.Version);
        Assert.Equal("import_job", file.Name);
        Assert.False(file.NoTransaction);
    }

    [Fact]
    public void The_migration_stays_out_of_the_frozen_common_namespace()
    {
        Assert.Empty(_source.Load(MigrationRunner.Common));
    }

    [Fact]
    public void The_migration_creates_the_three_import_tables()
    {
        Assert.Contains("CREATE TABLE import_job (", Sql);
        Assert.Contains("CREATE TABLE import_batch (", Sql);
        Assert.Contains("CREATE TABLE import_item (", Sql);
    }

    [Fact]
    public void The_import_item_key_is_importer_source_key_and_target_version()
    {
        Assert.Contains("PRIMARY KEY (importer_id, source_key, target_version)", Sql);
    }

    [Fact]
    public void The_migration_refers_to_no_shared_table()
    {
        // Namespace independence: the file must apply on an empty database, with no common/ table in place before it.
        Assert.DoesNotContain("REFERENCES gc", Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("gc_event", Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_state_the_store_writes_is_allowed_by_the_job_check()
    {
        var names = new[] { "staged", "dry_run", "committing", "paused", "stalled", "verifying", "verified", "failed_verify", "failed", "cancelled" };

        Assert.Equal(Enum.GetValues<ImportJobState>().Length, names.Length);
        foreach (var name in names)
        {
            Assert.Contains("'" + name + "'", Sql, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_migration_runs_in_one_transaction_and_has_no_concurrent_index()
    {
        Assert.DoesNotContain("CONCURRENTLY", Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(MigrationFile.NoTransactionMarker, Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void The_checksum_ignores_line_ending_style()
    {
        var lf = Sql.Replace("\r\n", "\n", StringComparison.Ordinal);
        var crlf = lf.Replace("\n", "\r\n", StringComparison.Ordinal);

        Assert.Equal(MigrationFile.ComputeChecksum(lf), MigrationFile.ComputeChecksum(crlf));
    }
}

/// <summary>The Postgres store refuses bad input before it opens a connection, so these checks need no database.</summary>
public class PostgresImportJobStoreValidationTests
{
    [Fact]
    public async Task A_bad_job_is_refused_before_the_database_is_touched()
    {
        // Port 1 is never listened on. If validation ran late, the call would fail with a connection error instead.
        await using var dataSource = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Username=none;Database=none");
        var store = new PostgresImportJobStore(dataSource);
        var bad = ImportStoreFixtures.NewJob("job-1") with { SourceManifest = [] };

        await Assert.ThrowsAsync<ArgumentException>(async () => await store.CreateJobAsync(bad, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_bad_batch_is_refused_before_the_database_is_touched()
    {
        await using var dataSource = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Username=none;Database=none");
        var store = new PostgresImportJobStore(dataSource);
        var items = new[] { ImportStoreFixtures.Item("job-1", "a") };
        var batch = ImportStoreFixtures.Batch("job-1", 0, ImportStoreFixtures.T0, items);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await store.RecordBatchAsync(batch, items, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_dry_run_with_a_bad_digest_is_refused_before_the_database_is_touched()
    {
        await using var dataSource = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Username=none;Database=none");
        var store = new PostgresImportJobStore(dataSource);

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await store.RecordDryRunAsync("job-1", "short", ImportStoreFixtures.Sha('b'), TestContext.Current.CancellationToken));
    }
}
