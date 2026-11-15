using Bazaar.Components.ManagedPostgres.Maintenance;

namespace Bazaar.Components.ManagedPostgres.Tests;

public sealed class PgDumpRestoreTests : IDisposable
{
    private readonly string _root = MaintenanceFixture.NewTempRoot();
    private readonly string _dump;

    public PgDumpRestoreTests()
    {
        _dump = Path.Combine(_root, "bazaar-pg18-test.dump");
        File.WriteAllText(_dump, "PGDMP-fake-payload");
        File.WriteAllText(_dump + ".sha256", $"{MaintenanceFixture.Sha256Of(_dump)}  bazaar-pg18-test.dump\n");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task A_dump_that_does_not_match_its_checksum_restores_nothing()
    {
        await File.WriteAllTextAsync(_dump, "tampered", TestContext.Current.CancellationToken);
        var layout = MaintenanceFixture.Layout(_root);
        var runner = new FakePostgresCommandRunner { OnRun = MaintenanceFixture.FakeDump };

        await Assert.ThrowsAsync<PgMaintenanceException>(() => PgDumpRestore.RunAsync(
            runner, layout, MaintenanceFixture.Connection(54320), _dump, TestContext.Current.CancellationToken));

        Assert.Empty(runner.Runs);
    }

    [Fact]
    public async Task An_existing_database_is_never_written_to()
    {
        var layout = MaintenanceFixture.Layout(_root);
        var runner = new FakePostgresCommandRunner { OnRun = (_, _) => new PostgresCommandResult(1, "database \"bazaar\" already exists") };

        var error = await Assert.ThrowsAsync<PgMaintenanceException>(() => PgDumpRestore.RunAsync(
            runner, layout, MaintenanceFixture.Connection(54320), _dump, TestContext.Current.CancellationToken));

        Assert.Contains("already exist", error.Message, StringComparison.Ordinal);
        Assert.Single(runner.Runs);
        Assert.Equal("createdb", Path.GetFileNameWithoutExtension(runner.Runs[0].FileName));
    }

    [Fact]
    public async Task A_failed_restore_drops_the_database_it_created()
    {
        var layout = MaintenanceFixture.Layout(_root);
        var runner = new FakePostgresCommandRunner
        {
            OnRun = (file, args) => Path.GetFileNameWithoutExtension(file) == "pg_restore"
                ? new PostgresCommandResult(1, "pg_restore: error: could not execute query")
                : new PostgresCommandResult(0, string.Empty),
        };

        await Assert.ThrowsAsync<PgMaintenanceException>(() => PgDumpRestore.RunAsync(
            runner, layout, MaintenanceFixture.Connection(54320), _dump, TestContext.Current.CancellationToken));

        Assert.Equal(["createdb", "pg_restore", "dropdb"], runner.Runs.Select(r => Path.GetFileNameWithoutExtension(r.FileName)));
        Assert.Contains("--exit-on-error", runner.Runs[1].Arguments);
    }

    [Fact]
    public async Task A_good_dump_restores_into_a_fresh_database_with_the_password_only_in_the_environment()
    {
        var layout = MaintenanceFixture.Layout(_root);
        var runner = new FakePostgresCommandRunner { OnRun = (_, _) => new PostgresCommandResult(0, string.Empty) };

        await PgDumpRestore.RunAsync(runner, layout, MaintenanceFixture.Connection(54320), _dump, TestContext.Current.CancellationToken);

        Assert.Equal(["createdb", "pg_restore"], runner.Runs.Select(r => Path.GetFileNameWithoutExtension(r.FileName)));
        Assert.All(runner.Environments, env => Assert.Equal(MaintenanceFixture.Password, env["PGPASSWORD"]));
        Assert.DoesNotContain(runner.Runs.SelectMany(r => r.Arguments), a => a.Contains(MaintenanceFixture.Password, StringComparison.Ordinal));
    }
}
