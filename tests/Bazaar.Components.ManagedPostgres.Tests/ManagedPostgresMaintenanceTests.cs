using System.Net;
using System.Net.Sockets;
using Bazaar.Components.ManagedPostgres.Maintenance;
using Bazaar.Components.ManagedPostgres.Secrets;
using Microsoft.Extensions.Options;

namespace Bazaar.Components.ManagedPostgres.Tests;

public sealed class ManagedPostgresMaintenanceTests
{
    [Fact]
    public async Task Backup_refuses_when_managed_mode_is_off()
    {
        var maintenance = Maintenance(new ManagedPostgresOptions { Mode = DatabaseMode.External }, new FakePostgresCommandRunner(), new ConstantStore("pw"));

        var error = await Assert.ThrowsAsync<PgMaintenanceException>(() => maintenance.BackupAsync(3, TestContext.Current.CancellationToken));

        Assert.Contains("Managed mode is off", error.Message);
    }

    [Fact]
    public async Task Upgrade_refuses_when_managed_mode_is_off()
    {
        var runner = new FakePostgresCommandRunner();
        var maintenance = Maintenance(new ManagedPostgresOptions { Mode = DatabaseMode.External }, runner, new ConstantStore("pw"));

        await Assert.ThrowsAsync<PgMaintenanceException>(() => maintenance.UpgradeAsync(19, 3, TestContext.Current.CancellationToken));
        Assert.Empty(runner.Runs);
    }

    [Fact]
    public async Task Backup_refuses_without_a_stored_password()
    {
        var maintenance = Maintenance(Managed(), new FakePostgresCommandRunner(), new ConstantStore(null));

        var error = await Assert.ThrowsAsync<PgMaintenanceException>(() => maintenance.BackupAsync(3, TestContext.Current.CancellationToken));

        Assert.Contains("No managed superuser password", error.Message);
    }

    [Fact]
    public async Task Upgrade_refuses_while_the_host_runs_the_cluster_and_touches_nothing()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var runner = new FakePostgresCommandRunner();
        var maintenance = Maintenance(Managed(port), runner, new ConstantStore("pw"));

        var error = await Assert.ThrowsAsync<PgMaintenanceException>(() => maintenance.UpgradeAsync(19, 3, TestContext.Current.CancellationToken));

        Assert.Contains("Stop the Bazaar host first", error.Message);
        Assert.Empty(runner.Runs);
    }

    [Fact]
    public async Task Upgrade_moves_one_major_at_a_time()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var maintenance = Maintenance(Managed(port), new FakePostgresCommandRunner(), new ConstantStore("pw"));

        // The port check runs first, so use a free port here to reach the major check.
        listener.Stop();
        var error = await Assert.ThrowsAsync<PgMaintenanceException>(() => maintenance.UpgradeAsync(20, 3, TestContext.Current.CancellationToken));

        Assert.Contains("one major at a time", error.Message);
    }

    private static ManagedPostgresOptions Managed(int port = 54320) => new()
    {
        Mode = DatabaseMode.Managed,
        DataRoot = Path.Combine("data", "root"),
        BundleRoot = Path.Combine("install", "pg"),
        Major = 18,
        Port = port,
    };

    private static ManagedPostgresMaintenance Maintenance(ManagedPostgresOptions options, FakePostgresCommandRunner runner, IManagedPasswordStore store) =>
        new(Options.Create(options), runner, store, TimeProvider.System);

    private sealed class ConstantStore(string? password) : IManagedPasswordStore
    {
        public Task<string?> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(password);

        public Task SaveAsync(string value, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
