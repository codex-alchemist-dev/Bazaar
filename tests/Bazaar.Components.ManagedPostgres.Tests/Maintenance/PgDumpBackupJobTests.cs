using Bazaar.Components.ManagedPostgres.Maintenance;

namespace Bazaar.Components.ManagedPostgres.Tests;

public sealed class PgDumpBackupJobTests : IDisposable
{
    private readonly string _root = MaintenanceFixture.NewTempRoot();
    private readonly string _backups;

    public PgDumpBackupJobTests()
    {
        _backups = Path.Combine(_root, "backups");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task A_good_dump_is_verified_renamed_and_checksummed_and_the_password_stays_out_of_arguments()
    {
        var layout = MaintenanceFixture.Layout(_root);
        var runner = new FakePostgresCommandRunner { OnRun = MaintenanceFixture.FakeDump };
        var job = new PgDumpBackupJob(runner, layout, new StepClock(new DateTimeOffset(2026, 11, 9, 7, 0, 0, TimeSpan.Zero)));

        var backup = await job.RunAsync(MaintenanceFixture.Connection(54320), _backups, keep: 3, TestContext.Current.CancellationToken);

        Assert.Equal(["pg_dump", "pg_restore"], runner.Runs.Select(r => Path.GetFileNameWithoutExtension(r.FileName)));
        Assert.Equal(MaintenanceFixture.Password, runner.Environments[0]["PGPASSWORD"]);
        Assert.DoesNotContain(runner.Runs.SelectMany(r => r.Arguments), a => a.Contains(MaintenanceFixture.Password, StringComparison.Ordinal));
        Assert.Equal("bazaar-pg18-20261109T070000000Z.dump", Path.GetFileName(backup.DumpPath));
        Assert.True(File.Exists(backup.DumpPath));
        Assert.False(File.Exists(backup.DumpPath + ".partial"));
        Assert.Equal(MaintenanceFixture.Sha256Of(backup.DumpPath), backup.Sha256);
        Assert.Equal(
            $"{backup.Sha256}  bazaar-pg18-20261109T070000000Z.dump\n",
            await File.ReadAllTextAsync(backup.DumpPath + ".sha256", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_dump_that_pg_restore_cannot_read_leaves_no_file_behind()
    {
        var layout = MaintenanceFixture.Layout(_root);
        var runner = new FakePostgresCommandRunner
        {
            OnRun = (file, args) => Path.GetFileNameWithoutExtension(file) == "pg_restore"
                ? new PostgresCommandResult(1, "pg_restore: error: input file is too short")
                : MaintenanceFixture.FakeDump(file, args),
        };
        var job = new PgDumpBackupJob(runner, layout, new StepClock(DateTimeOffset.UtcNow));

        var error = await Assert.ThrowsAsync<PgMaintenanceException>(() =>
            job.RunAsync(MaintenanceFixture.Connection(54320), _backups, keep: 3, TestContext.Current.CancellationToken));

        Assert.Contains("could not read the new dump", error.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(_backups));
    }

    [Fact]
    public async Task A_failed_pg_dump_is_reported_with_its_output_and_writes_nothing()
    {
        var layout = MaintenanceFixture.Layout(_root);
        var runner = new FakePostgresCommandRunner
        {
            OnRun = (_, _) => new PostgresCommandResult(1, "pg_dump: error: connection refused"),
        };
        var job = new PgDumpBackupJob(runner, layout, new StepClock(DateTimeOffset.UtcNow));

        var error = await Assert.ThrowsAsync<PgMaintenanceException>(() =>
            job.RunAsync(MaintenanceFixture.Connection(54320), _backups, keep: 3, TestContext.Current.CancellationToken));

        Assert.Contains("connection refused", error.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(_backups));
    }

    [Fact]
    public async Task Retention_keeps_the_newest_dumps_for_that_major_and_their_checksums()
    {
        var layout = MaintenanceFixture.Layout(_root);
        var clock = new StepClock(new DateTimeOffset(2026, 11, 9, 7, 0, 0, TimeSpan.Zero));
        var runner = new FakePostgresCommandRunner { OnRun = MaintenanceFixture.FakeDump };
        var job = new PgDumpBackupJob(runner, layout, clock);

        for (var i = 0; i < 3; i++)
        {
            await job.RunAsync(MaintenanceFixture.Connection(54320), _backups, keep: 2, TestContext.Current.CancellationToken);
            clock.Now = clock.Now.AddMinutes(1);
        }

        var dumps = Directory.EnumerateFiles(_backups, "*.dump").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(
            ["bazaar-pg18-20261109T070100000Z.dump", "bazaar-pg18-20261109T070200000Z.dump"],
            dumps);
        Assert.Equal(2, Directory.EnumerateFiles(_backups, "*.sha256").Count());
    }

    [Fact]
    public async Task Keep_below_one_is_refused()
    {
        var job = new PgDumpBackupJob(new FakePostgresCommandRunner(), MaintenanceFixture.Layout(_root), TimeProvider.System);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            job.RunAsync(MaintenanceFixture.Connection(54320), _backups, keep: 0, TestContext.Current.CancellationToken));
    }
}
