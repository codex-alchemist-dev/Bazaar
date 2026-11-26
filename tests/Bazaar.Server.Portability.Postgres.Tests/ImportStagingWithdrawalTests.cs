using Bazaar.Server.Data.Migrations;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Portability.Postgres.Tests;

/// <summary>The 0002 migrations as embedded in their assemblies. No database is needed.</summary>
public class ImportStagingMigrationTests
{
    private static readonly EmbeddedMigrationSource _portability = new(typeof(PostgresImportJobStore).Assembly);
    private static readonly EmbeddedMigrationSource _server = new(typeof(Bazaar.Server.AssemblyMarker).Assembly);

    private static string Sql => _portability.Load("portability").Single(f => f.Version == 2).Sql;

    [Fact]
    public void Portability_0002_creates_staging_and_withdrawal_tables()
    {
        var file = _portability.Load("portability").Single(f => f.Version == 2);

        Assert.Equal("import_staging_withdrawal", file.Name);
        Assert.Contains("CREATE TABLE import_staging (", Sql);
        Assert.Contains("CREATE TABLE import_withdrawal (", Sql);
        Assert.Contains("PRIMARY KEY (job_id, source_key)", Sql);
    }

    [Fact]
    public void The_migration_stays_inside_its_own_namespace()
    {
        Assert.Empty(_portability.Load(MigrationRunner.Common));
        Assert.DoesNotContain("REFERENCES gc", Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("REFERENCES tasks", Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Kanban_0002_adds_a_nullable_import_job_id_in_the_kanban_namespace()
    {
        var file = _server.Load("kanban").Single(f => f.Version == 2);

        Assert.Equal("task_import_job", file.Name);
        Assert.Contains("ADD COLUMN import_job_id text", file.Sql);
        Assert.DoesNotContain("NOT NULL", file.Sql.Replace("IS NOT NULL", "", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.DoesNotContain("REFERENCES", file.Sql, StringComparison.Ordinal);
    }
}

/// <summary>Staging records against PostgreSQL. The file deleter is a fake, since this project holds no file code.</summary>
public sealed class PostgresImportStagingStoreTests
{
    private static readonly DateTimeOffset _now = ImportStoreFixtures.T0.AddDays(5);

    private sealed class Deleter
    {
        public List<string> Jobs { get; } = [];

        public bool Fail { get; set; }

        public Task DeleteAsync(string jobId, CancellationToken ct)
        {
            if (Fail)
            {
                throw new IOException("disk busy");
            }
            Jobs.Add(jobId);
            return Task.CompletedTask;
        }
    }

    private static async Task<PgTestDatabase> OpenAsync(CancellationToken ct)
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        return await PgTestDatabase.CreateAsync("staging", PostgresImportJobStoreTests.MigrateAsync, ct);
    }

    private static ImportStagingEntry Entry(string job, string key, long size = 100, string path = "")
        => new(job, key, path.Length > 0 ? path : "files/" + key + ".txt", size, ImportStoreFixtures.Sha('a'), ImportStoreFixtures.T0);

    private static async Task VerifyAsync(NpgsqlDataSource ds, string jobId, DateTimeOffset at, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("UPDATE import_job SET state = 'verified', ended_at = $2 WHERE job_id = $1");
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = jobId });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = at });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    [Fact]
    public async Task Records_lists_and_sums_staged_files_per_job()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenAsync(ct);
        var jobs = new PostgresImportJobStore(db.DataSource);
        await jobs.CreateJobAsync(ImportStoreFixtures.NewJob("job-1"), ct);
        await jobs.CreateJobAsync(ImportStoreFixtures.NewJob("job-2"), ct);
        var store = new PostgresImportStagingStore(db.DataSource, new Deleter().DeleteAsync);

        await store.RecordAsync(Entry("job-1", "b", 30), ct);
        await store.RecordAsync(Entry("job-1", "a", 70), ct);
        await store.RecordAsync(Entry("job-2", "x", 5), ct);

        Assert.Equal(["a", "b"], (await store.ListAsync("job-1", ct)).Select(e => e.SourceKey));
        Assert.Equal(100, await store.StagedBytesAsync("job-1", ct));
        Assert.Equal(5, await store.StagedBytesAsync("job-2", ct));
        Assert.Equal(0, await store.StagedBytesAsync("none", ct));
    }

    [Fact]
    public async Task A_repeated_record_replaces_the_row()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenAsync(ct);
        await new PostgresImportJobStore(db.DataSource).CreateJobAsync(ImportStoreFixtures.NewJob("job-1"), ct);
        var store = new PostgresImportStagingStore(db.DataSource, new Deleter().DeleteAsync);

        await store.RecordAsync(Entry("job-1", "a", 10), ct);
        await store.RecordAsync(Entry("job-1", "a", 40), ct);

        Assert.Equal(40, await store.StagedBytesAsync("job-1", ct));
        Assert.Single(await store.ListAsync("job-1", ct));
    }

    [Fact]
    public async Task A_file_for_an_unknown_job_is_a_conflict()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenAsync(ct);
        var store = new PostgresImportStagingStore(db.DataSource, new Deleter().DeleteAsync);

        await Assert.ThrowsAsync<ImportStoreConflictException>(async () => await store.RecordAsync(Entry("missing", "a"), ct));
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("\\share\\x")]
    [InlineData("a/../../b")]
    [InlineData("..")]
    [InlineData(" ")]
    public async Task A_path_outside_the_job_directory_is_refused_before_the_database_is_touched(string path)
    {
        await using var ds = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Username=none;Database=none");
        var store = new PostgresImportStagingStore(ds, new Deleter().DeleteAsync);

        await Assert.ThrowsAsync<ArgumentException>(async () => await store.RecordAsync(Entry("job-1", "a", 1, path), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Only_verified_jobs_with_live_staging_are_listed_for_the_sweep()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenAsync(ct);
        var jobs = new PostgresImportJobStore(db.DataSource);
        foreach (var id in new[] { "verified-1", "open-1", "verified-no-staging" })
        {
            await jobs.CreateJobAsync(ImportStoreFixtures.NewJob(id), ct);
        }
        var store = new PostgresImportStagingStore(db.DataSource, new Deleter().DeleteAsync);
        await store.RecordAsync(Entry("verified-1", "a"), ct);
        await store.RecordAsync(Entry("open-1", "a"), ct);
        await VerifyAsync(db.DataSource, "verified-1", ImportStoreFixtures.T0.AddDays(1), ct);
        await VerifyAsync(db.DataSource, "verified-no-staging", ImportStoreFixtures.T0.AddDays(1), ct);

        var listed = await store.ListVerifiedAsync(ct);

        var only = Assert.Single(listed);
        Assert.Equal(("verified-1", ImportStoreFixtures.T0.AddDays(1)), only);
    }

    [Fact]
    public async Task Sweep_deletes_files_then_marks_rows_and_keeps_them_as_history()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenAsync(ct);
        await new PostgresImportJobStore(db.DataSource).CreateJobAsync(ImportStoreFixtures.NewJob("job-1"), ct);
        var deleter = new Deleter();
        var store = new PostgresImportStagingStore(db.DataSource, deleter.DeleteAsync, new FixedTime(_now));
        await store.RecordAsync(Entry("job-1", "a", 10), ct);
        await VerifyAsync(db.DataSource, "job-1", ImportStoreFixtures.T0.AddDays(1), ct);

        var swept = await ImportStagingSweeper.SweepAsync(store, StagingRetention.Default, new FixedTime(_now), ct);

        Assert.Equal(["job-1"], swept);
        Assert.Equal(["job-1"], deleter.Jobs);
        Assert.Empty(await store.ListAsync("job-1", ct));
        Assert.Equal(0, await store.StagedBytesAsync("job-1", ct));
        await using var cmd = db.DataSource.CreateCommand("SELECT count(*), count(deleted_at) FROM import_staging");
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        Assert.True(await reader.ReadAsync(ct));
        Assert.Equal((1L, 1L), (reader.GetInt64(0), reader.GetInt64(1)));
        Assert.Empty(await store.ListVerifiedAsync(ct));
    }

    [Fact]
    public async Task A_failing_file_delete_leaves_the_rows_live_for_the_next_sweep()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenAsync(ct);
        await new PostgresImportJobStore(db.DataSource).CreateJobAsync(ImportStoreFixtures.NewJob("job-1"), ct);
        var deleter = new Deleter { Fail = true };
        var store = new PostgresImportStagingStore(db.DataSource, deleter.DeleteAsync, new FixedTime(_now));
        await store.RecordAsync(Entry("job-1", "a", 10), ct);
        await VerifyAsync(db.DataSource, "job-1", ImportStoreFixtures.T0.AddDays(1), ct);

        Assert.Empty(await ImportStagingSweeper.SweepAsync(store, StagingRetention.Default, new FixedTime(_now), ct));
        Assert.Single(await store.ListAsync("job-1", ct));

        deleter.Fail = false;
        Assert.Equal(["job-1"], await ImportStagingSweeper.SweepAsync(store, StagingRetention.Default, new FixedTime(_now), ct));
    }

    [Fact]
    public async Task Staging_inside_the_retention_window_is_kept()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await OpenAsync(ct);
        await new PostgresImportJobStore(db.DataSource).CreateJobAsync(ImportStoreFixtures.NewJob("job-1"), ct);
        var deleter = new Deleter();
        var store = new PostgresImportStagingStore(db.DataSource, deleter.DeleteAsync);
        await store.RecordAsync(Entry("job-1", "a", 10), ct);
        await VerifyAsync(db.DataSource, "job-1", ImportStoreFixtures.T0.AddDays(1), ct);

        var swept = await ImportStagingSweeper.SweepAsync(store, StagingRetention.Default, new FixedTime(ImportStoreFixtures.T0.AddDays(2)), ct);

        Assert.Empty(swept);
        Assert.Empty(deleter.Jobs);
    }
}

internal sealed class FixedTime(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
