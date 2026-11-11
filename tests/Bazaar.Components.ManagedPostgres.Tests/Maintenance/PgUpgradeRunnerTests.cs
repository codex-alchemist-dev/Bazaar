using Bazaar.Components.ManagedPostgres.Maintenance;

namespace Bazaar.Components.ManagedPostgres.Tests;

public sealed class PgUpgradeRunnerTests : IDisposable
{
    private readonly string _root = MaintenanceFixture.NewTempRoot();
    private readonly string _backups;
    private readonly List<string> _events = [];

    public PgUpgradeRunnerTests()
    {
        _backups = Path.Combine(_root, "backups");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task The_upgrade_dumps_then_stops_then_checks_then_copies_and_never_links()
    {
        var port = ProcessTestSupport.FreePort();
        var layout = MaintenanceFixture.Layout(_root);
        var runner = Runner((file, args) =>
        {
            _events.Add(Path.GetFileNameWithoutExtension(file) + (args.Contains("--check") ? " --check" : string.Empty));
            return MaintenanceFixture.FakeDump(file, args);
        });
        var upgrade = new PgUpgradeRunner(runner, layout, new PgDumpBackupJob(runner, layout, TimeProvider.System));

        var result = await upgrade.RunAsync(
            new PgUpgradeRequest(MaintenanceFixture.Connection(port), 19, _backups, KeepDumps: 3),
            _ => { _events.Add("stop"); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);

        Assert.Equal(["pg_dump", "pg_restore", "stop", "pg_upgrade --check", "pg_upgrade"], _events);
        var real = runner.Runs[^1].Arguments;
        Assert.DoesNotContain("--check", real);
        Assert.DoesNotContain("--link", real);
        Assert.True(File.Exists(result.Backup.DumpPath));
        Assert.Equal(18, result.FromMajor);
        Assert.Equal(19, result.ToMajor);
    }

    [Fact]
    public async Task A_failing_check_stops_the_run_before_any_data_file_is_touched()
    {
        var port = ProcessTestSupport.FreePort();
        var layout = MaintenanceFixture.Layout(_root);
        var runner = Runner((file, args) =>
            args.Contains("--check")
                ? new PostgresCommandResult(1, "Database user is not the install user")
                : MaintenanceFixture.FakeDump(file, args));
        var upgrade = new PgUpgradeRunner(runner, layout, new PgDumpBackupJob(runner, layout, TimeProvider.System));

        var error = await Assert.ThrowsAsync<PgMaintenanceException>(() => upgrade.RunAsync(
            new PgUpgradeRequest(MaintenanceFixture.Connection(port), 19, _backups, KeepDumps: 3),
            _ => Task.CompletedTask,
            TestContext.Current.CancellationToken));

        Assert.Contains("no data was changed", error.Message, StringComparison.Ordinal);
        Assert.Single(runner.Runs, r => Path.GetFileNameWithoutExtension(r.FileName) == "pg_upgrade");
    }

    [Fact]
    public async Task A_cluster_that_still_listens_after_the_stop_request_blocks_the_upgrade()
    {
        var port = ProcessTestSupport.FreePort();
        using var listener = ProcessTestSupport.ListenOn(port);
        var layout = MaintenanceFixture.Layout(_root);
        var runner = Runner(MaintenanceFixture.FakeDump);
        var upgrade = new PgUpgradeRunner(runner, layout, new PgDumpBackupJob(runner, layout, TimeProvider.System));

        await Assert.ThrowsAsync<PgMaintenanceException>(() => upgrade.RunAsync(
            new PgUpgradeRequest(MaintenanceFixture.Connection(port), 19, _backups, KeepDumps: 3),
            _ => Task.CompletedTask,
            TestContext.Current.CancellationToken));

        Assert.DoesNotContain(runner.Runs, r => Path.GetFileNameWithoutExtension(r.FileName) == "pg_upgrade");
    }

    [Fact]
    public async Task A_new_cluster_that_was_never_initdb_ed_is_refused_before_the_dump()
    {
        var layout = MaintenanceFixture.Layout(_root, initialiseNew: false);
        var runner = Runner(MaintenanceFixture.FakeDump);
        var upgrade = new PgUpgradeRunner(runner, layout, new PgDumpBackupJob(runner, layout, TimeProvider.System));

        var error = await Assert.ThrowsAsync<PgMaintenanceException>(() => upgrade.RunAsync(
            new PgUpgradeRequest(MaintenanceFixture.Connection(ProcessTestSupport.FreePort()), 19, _backups, KeepDumps: 3),
            _ => Task.CompletedTask,
            TestContext.Current.CancellationToken));

        Assert.Contains("initdb", error.Message, StringComparison.Ordinal);
        Assert.Empty(runner.Runs);
    }

    [Fact]
    public async Task Moving_more_than_one_major_is_refused()
    {
        var layout = MaintenanceFixture.Layout(_root);
        var runner = Runner(MaintenanceFixture.FakeDump);
        var upgrade = new PgUpgradeRunner(runner, layout, new PgDumpBackupJob(runner, layout, TimeProvider.System));

        await Assert.ThrowsAnyAsync<Exception>(() => upgrade.RunAsync(
            new PgUpgradeRequest(MaintenanceFixture.Connection(ProcessTestSupport.FreePort()), 20, _backups, KeepDumps: 3),
            _ => Task.CompletedTask,
            TestContext.Current.CancellationToken));
    }

    private static FakePostgresCommandRunner Runner(Func<string, IReadOnlyList<string>, PostgresCommandResult> onRun) =>
        new() { OnRun = onRun };
}
