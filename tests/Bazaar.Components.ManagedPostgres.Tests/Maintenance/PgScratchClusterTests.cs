using Bazaar.Components.ManagedPostgres.Maintenance;

namespace Bazaar.Components.ManagedPostgres.Tests;

public sealed class PgScratchClusterTests : IDisposable
{
    private readonly string _root = MaintenanceFixture.NewTempRoot();

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    private static FakePostgresCommandRunner NewRunner()
    {
        var runner = new FakePostgresCommandRunner();
        runner.OnRun = (file, args) =>
        {
            if (args.Contains("fast"))
            {
                foreach (var child in runner.Children)
                {
                    child.Exit(0);
                }
            }

            return FakePostgresCommandRunner.SeedInitDb(file, args);
        };
        return runner;
    }

    private static Task<PgScratchCluster> CreateAsync(FakePostgresCommandRunner runner, Bazaar.Components.ManagedPostgres.Bundle.BundleLayout layout, string scratchRoot, string name) =>
        PgScratchCluster.CreateAsync(runner, layout, 19, scratchRoot, name, TestContext.Current.CancellationToken, new FakePortProbe(runner));

    private string ScratchRoot => Path.Combine(_root, "scratch");

    [Fact]
    public async Task Create_initialises_a_cluster_outside_production_and_dispose_removes_it()
    {
        var layout = MaintenanceFixture.Layout(_root);
        var runner = NewRunner();

        var cluster = await CreateAsync(runner, layout, ScratchRoot, "dryrun-1");

        Assert.True(Directory.Exists(cluster.DataDirectory));
        Assert.StartsWith(Path.GetFullPath(ScratchRoot), cluster.DataDirectory, StringComparison.Ordinal);
        Assert.Equal(19, cluster.Connection.Major);
        Assert.Equal(PgScratchCluster.DatabaseName, cluster.Connection.Database);
        Assert.False(string.IsNullOrEmpty(cluster.Connection.Password));
        Assert.NotEqual(54320, cluster.Connection.Port);

        await cluster.DisposeAsync();
        await cluster.DisposeAsync();

        Assert.False(Directory.Exists(cluster.DataDirectory));
        Assert.False(File.Exists(cluster.DataDirectory + ".bootstrap-pending"));
    }

    [Fact]
    public async Task Create_refuses_an_existing_folder_and_leaves_it_alone()
    {
        var layout = MaintenanceFixture.Layout(_root);
        var existing = Path.Combine(ScratchRoot, "taken");
        Directory.CreateDirectory(existing);
        File.WriteAllText(Path.Combine(existing, "keep.txt"), "x");

        await Assert.ThrowsAsync<PgMaintenanceException>(() =>
            CreateAsync(NewRunner(), layout, ScratchRoot, "taken"));

        Assert.True(File.Exists(Path.Combine(existing, "keep.txt")));
    }

    [Fact]
    public async Task Create_refuses_a_scratch_root_that_overlaps_a_production_data_directory()
    {
        var layout = MaintenanceFixture.Layout(_root);
        var production = layout.Resolve(18).DataDirectory;

        await Assert.ThrowsAsync<PgMaintenanceException>(() =>
            CreateAsync(NewRunner(), layout, production, "inside"));
        await Assert.ThrowsAsync<PgMaintenanceException>(() =>
            CreateAsync(NewRunner(), layout, Path.GetDirectoryName(production)!, "data"));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("")]
    [InlineData("with space")]
    public async Task Create_rejects_unsafe_names(string name)
    {
        var layout = MaintenanceFixture.Layout(_root);

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            CreateAsync(NewRunner(), layout, ScratchRoot, name));
    }

    [Fact]
    public async Task Create_cleans_up_when_initdb_fails()
    {
        var layout = MaintenanceFixture.Layout(_root);
        var runner = NewRunner();
        runner.OnRun = (file, args) =>
        {
            var result = FakePostgresCommandRunner.SeedInitDb(file, args);
            return Path.GetFileNameWithoutExtension(file) == "initdb" ? new PostgresCommandResult(1, "boom") : result;
        };

        await Assert.ThrowsAnyAsync<Exception>(() =>
            CreateAsync(runner, layout, ScratchRoot, "failing"));

        Assert.False(Directory.Exists(Path.Combine(ScratchRoot, "failing")));
        Assert.False(File.Exists(Path.Combine(ScratchRoot, "failing") + ".bootstrap-pending"));
    }

    [Fact]
    public async Task Restore_creates_the_database_then_restores_with_the_scratch_password_in_the_environment_only()
    {
        var layout = MaintenanceFixture.Layout(_root);
        var runner = NewRunner();
        await using var cluster = await CreateAsync(runner, layout, ScratchRoot, "restore");
        var dump = Path.Combine(_root, "prod.dump");
        File.WriteAllText(dump, "PGDMP-fake");
        var before = runner.Runs.Count;

        await cluster.RestoreAsync(dump, TestContext.Current.CancellationToken);

        var tools = runner.Runs.Skip(before).Select(r => Path.GetFileNameWithoutExtension(r.FileName)).ToArray();
        Assert.Equal(["createdb", "pg_restore"], tools);
        Assert.All(runner.Runs.Skip(before), run => Assert.DoesNotContain(cluster.Connection.Password, string.Join(' ', run.Arguments), StringComparison.Ordinal));
        Assert.Equal(cluster.Connection.Password, runner.Environments[^1]["PGPASSWORD"]);
    }

    [Fact]
    public async Task Restore_after_dispose_throws()
    {
        var layout = MaintenanceFixture.Layout(_root);
        var cluster = await CreateAsync(NewRunner(), layout, ScratchRoot, "late");
        await cluster.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => cluster.RestoreAsync("x", TestContext.Current.CancellationToken));
    }
}
