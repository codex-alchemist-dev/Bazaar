using Bazaar.Components.ManagedPostgres.Bundle;
using Bazaar.Components.ManagedPostgres.Maintenance;

namespace Bazaar.Components.ManagedPostgres.Tests;

/// <summary>
/// Real PostgreSQL tools. Set BAZAAR_TEST_PG_BIN to a PostgreSQL bin directory to run these; they are skipped otherwise.
/// Both bundle majors point at that one bin directory through symlinks, so the upgrade runs for real with pg_upgrade
/// (same-major pg_upgrade is a valid check of the mechanics; the 18 to 19 policy is covered by the planner tests).
/// </summary>
public sealed class PgMaintenanceIntegrationTests : IAsyncDisposable
{
    private const string _binVariable = "BAZAAR_TEST_PG_BIN";
    private readonly string _root = MaintenanceFixture.NewTempRoot();
    private readonly List<PostgresProcessController> _controllers = [];

    /// <summary>Clusters stop before their data folder is removed, so no server writes into a deleted directory.</summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var controller in _controllers)
        {
            await controller.DisposeAsync();
        }

        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task A_dump_restores_into_a_fresh_database_with_every_row()
    {
        var bin = RequireBin();
        var layout = BundleOver(bin);
        var runner = new ProcessPostgresCommandRunner();
        var cluster = await StartClusterAsync(bin, layout.Resolve(18).DataDirectory, port: ProcessTestSupport.FreePort());
        var connection = cluster.Connection with { Database = "bazaar" };

        await RunSqlAsync(runner, layout, connection, "bazaar", "create table t(v int); insert into t values (1), (2), (3);");
        var backup = await new PgDumpBackupJob(runner, layout, TimeProvider.System).RunAsync(
            connection, Path.Combine(_root, "backups"), keep: 3, TestContext.Current.CancellationToken);

        await PgDumpRestore.RunAsync(runner, layout, connection with { Database = "restored" }, backup.DumpPath, TestContext.Current.CancellationToken);

        Assert.Equal("3", await RunSqlAsync(runner, layout, connection, "restored", "select count(*) from t;"));
    }

    [Fact]
    public async Task A_tampered_dump_is_refused_and_nothing_is_created()
    {
        var bin = RequireBin();
        var layout = BundleOver(bin);
        var runner = new ProcessPostgresCommandRunner();
        var cluster = await StartClusterAsync(bin, layout.Resolve(18).DataDirectory, port: ProcessTestSupport.FreePort());
        var connection = cluster.Connection with { Database = "bazaar" };
        await RunSqlAsync(runner, layout, connection, "bazaar", "create table t(v int);");
        var backup = await new PgDumpBackupJob(runner, layout, TimeProvider.System).RunAsync(
            connection, Path.Combine(_root, "backups"), keep: 3, TestContext.Current.CancellationToken);
        await File.AppendAllTextAsync(backup.DumpPath, "x", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<PgMaintenanceException>(() => PgDumpRestore.RunAsync(
            runner, layout, connection with { Database = "restored" }, backup.DumpPath, TestContext.Current.CancellationToken));

        Assert.False(await DatabaseExistsAsync(runner, layout, connection, "restored"));
    }

    [Fact]
    public async Task An_upgrade_moves_every_row_to_the_new_cluster_and_keeps_the_old_data_directory()
    {
        var bin = RequireBin();
        var layout = BundleOver(bin);
        var runner = new ProcessPostgresCommandRunner();
        var oldPort = ProcessTestSupport.FreePort();
        var newPort = ProcessTestSupport.FreePort();
        var old = await StartClusterAsync(bin, layout.Resolve(18).DataDirectory, oldPort);
        var oldConnection = old.Connection with { Database = "bazaar" };
        await RunSqlAsync(runner, layout, oldConnection, "bazaar", "create table t(v int); insert into t values (1), (2), (3);");

        // The new cluster is initialised with the old superuser password, as an upgrade preparation step would do.
        var newCluster = ProcessControllerFor(bin, layout.Resolve(19).DataDirectory, newPort);
        await newCluster.InitDbWithPasswordAsync(oldConnection.Password, TestContext.Current.CancellationToken);

        var upgrade = new PgUpgradeRunner(runner, layout, new PgDumpBackupJob(runner, layout, TimeProvider.System));
        var result = await upgrade.RunAsync(
            new PgUpgradeRequest(oldConnection, ToMajor: 19, Path.Combine(_root, "backups"), KeepDumps: 3),
            ct => old.Controller.StopAsync(ct),
            TestContext.Current.CancellationToken);

        Assert.True(File.Exists(result.Backup.DumpPath));
        await newCluster.StartAsync(TestContext.Current.CancellationToken);
        // pg_upgrade carries the role and its scram verifier over, so the old superuser password opens the new cluster.
        var upgraded = new PgConnection(19, newPort, oldConnection.User, oldConnection.Password, "bazaar");
        Assert.Equal("3", await RunSqlAsync(runner, layout, upgraded, "bazaar", "select count(*) from t;"));
        Assert.True(File.Exists(Path.Combine(layout.Resolve(18).DataDirectory, "PG_VERSION")));
    }

    private sealed record StartedCluster(PostgresProcessController Controller, PgConnection Connection);

    private static string RequireBin()
    {
        var bin = Environment.GetEnvironmentVariable(_binVariable);
        if (string.IsNullOrWhiteSpace(bin))
        {
            Assert.Skip($"Set {_binVariable} to a PostgreSQL bin directory to run this test.");
        }

        return bin;
    }

    /// <summary>A bundle root whose 18 and 19 bin folders both point at the given bin directory.</summary>
    private BundleLayout BundleOver(string bin)
    {
        var pgRoot = Path.Combine(_root, "pg");
        foreach (var major in new[] { "18", "19" })
        {
            Directory.CreateDirectory(Path.Combine(pgRoot, major));
            var link = Path.Combine(pgRoot, major, "bin");
            if (!Directory.Exists(link))
            {
                Directory.CreateSymbolicLink(link, bin);
            }
        }

        return new BundleLayout(pgRoot, Path.Combine(_root, "data"));
    }

    private PostgresProcessController ProcessControllerFor(string bin, string dataDirectory, int port)
    {
        var options = ProcessTestSupport.OptionsFor(dataDirectory, port);
        options.BinDirectory = bin;
        options.StartupTimeout = TimeSpan.FromSeconds(60);
        options.ShutdownTimeout = TimeSpan.FromSeconds(15);
        var controller = new PostgresProcessController(options, new ProcessPostgresCommandRunner());
        _controllers.Add(controller);
        return controller;
    }

    private async Task<StartedCluster> StartClusterAsync(string bin, string dataDirectory, int port)
    {
        var controller = ProcessControllerFor(bin, dataDirectory, port);
        string? password = null;
        await controller.InitDbAsync((value, _) => { password = value; return ValueTask.CompletedTask; }, TestContext.Current.CancellationToken);
        await controller.StartAsync(TestContext.Current.CancellationToken);
        var connection = new PgConnection(18, port, "bazaar_admin", password!, "postgres");
        await RunSqlAsync(new ProcessPostgresCommandRunner(), BundleOver(bin), connection, "postgres", "create database bazaar;");
        return new StartedCluster(controller, connection);
    }

    private static async Task<bool> DatabaseExistsAsync(IPostgresCommandRunner runner, BundleLayout layout, PgConnection connection, string database)
    {
        var result = await RunSqlAsync(runner, layout, connection, "postgres", $"select count(*) from pg_database where datname = '{database}';");
        return result == "1";
    }

    private static async Task<string> RunSqlAsync(IPostgresCommandRunner runner, BundleLayout layout, PgConnection connection, string database, string sql)
    {
        var command = PgDumpBackupPlanner.PlanSql(layout, connection.Major, connection.Port, connection.User, database, sql);
        var result = await runner.RunAsync(command.FileName, command.Arguments, TestContext.Current.CancellationToken, connection.Environment);
        if (result.ExitCode != 0)
        {
            throw new PgMaintenanceException($"psql failed: {result.Output.Trim()}");
        }

        return result.Output.Trim();
    }
}
