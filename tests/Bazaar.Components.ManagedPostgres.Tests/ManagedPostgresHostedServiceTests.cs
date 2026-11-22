using Bazaar.Components.ManagedPostgres.Secrets;
using Bazaar.Server.DataVolume;
using Microsoft.Extensions.Options;

namespace Bazaar.Components.ManagedPostgres.Tests;

public sealed class ManagedPostgresHostedServiceTests
{
    [Fact]
    public async Task External_mode_runs_nothing_and_publishes_no_endpoint()
    {
        var runner = new FakePostgresCommandRunner();
        var endpoint = new ManagedPostgresEndpoint();
        var service = CreateService(new ManagedPostgresOptions { Mode = DatabaseMode.External }, runner, endpoint);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);

        Assert.Empty(runner.Runs);
        Assert.Empty(runner.Children);
        Assert.Null(endpoint.Current);
    }

    [Fact]
    public async Task Managed_mode_without_a_password_store_refuses_to_start()
    {
        var runner = new FakePostgresCommandRunner();
        var options = ManagedOptions();
        var service = new ManagedPostgresHostedService(Options.Create(options), runner, [], new ManagedPostgresEndpoint());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("password store", error.Message, StringComparison.Ordinal);
        Assert.Empty(runner.Runs);
    }

    [Fact]
    public async Task Managed_mode_rejects_an_unsupported_major_before_touching_the_cluster()
    {
        var runner = new FakePostgresCommandRunner();
        var options = ManagedOptions();
        options.Major = 17;
        var service = new ManagedPostgresHostedService(Options.Create(options), runner, [new MemoryPasswordStore()], new ManagedPostgresEndpoint());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.StartAsync(TestContext.Current.CancellationToken));

        Assert.Empty(runner.Runs);
    }

    [Fact]
    public async Task A_refusing_data_volume_guard_stops_the_start_before_any_postgres_command()
    {
        var runner = new FakePostgresCommandRunner();
        var guard = new StubGuard { Refusal = DataVolumeRefusal.NotEncrypted };
        var service = new ManagedPostgresHostedService(Options.Create(ManagedOptions()), runner, [new MemoryPasswordStore()], new ManagedPostgresEndpoint(), [guard]);

        var refused = await Assert.ThrowsAsync<DataVolumeRefusedException>(() => service.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal(DataVolumeRefusal.NotEncrypted, refused.Reason);
        Assert.Empty(runner.Runs);
        Assert.Empty(runner.Children);
    }

    [Fact]
    public async Task With_a_data_volume_guard_a_missing_cluster_is_refused_and_initdb_does_not_run()
    {
        var runner = new FakePostgresCommandRunner();
        var options = ManagedOptions();
        options.DataRoot = Path.Combine(Path.GetTempPath(), "bazaar-data-test-" + Guid.CreateVersion7().ToString("N"));
        var guard = new StubGuard();
        var service = new ManagedPostgresHostedService(Options.Create(options), runner, [new MemoryPasswordStore()], new ManagedPostgresEndpoint(), [guard]);

        var refused = await Assert.ThrowsAsync<DataVolumeRefusedException>(() => service.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal(DataVolumeRefusal.NotInitialised, refused.Reason);
        Assert.Empty(runner.Runs);
        Assert.False(Directory.Exists(options.DataRoot));
        Assert.Contains(guard.CheckedPaths, path => path.EndsWith("pg_wal", StringComparison.Ordinal));
    }

    private sealed class StubGuard : IDataVolumeGuard
    {
        public DataVolumeRefusal? Refusal { get; init; }

        public List<string> CheckedPaths { get; } = [];

        public ValueTask<DataVolumeVerification> VerifyAsync(CancellationToken cancellationToken)
        {
            if (Refusal is { } reason)
                throw new DataVolumeRefusedException(reason, "refused by the test");

            var descriptor = new DataVolumeDescriptor(1, "install", "volume", "fake", "file", "container", DataVolumeCiphers.XtsAes256, InstanceClass.Production, "kek", [], [1], DateTimeOffset.UnixEpoch);
            var status = new DataVolumeStatus(true, "volume", true, true, DataVolumeCiphers.XtsAes256);
            return ValueTask.FromResult(new DataVolumeVerification(descriptor, status, null));
        }

        public void EnsureInsideDataRoot(IEnumerable<string> paths) => CheckedPaths.AddRange(paths);
    }

    private static ManagedPostgresHostedService CreateService(ManagedPostgresOptions options, FakePostgresCommandRunner runner, ManagedPostgresEndpoint endpoint) =>
        new(Options.Create(options), runner, [new MemoryPasswordStore()], endpoint);

    private static ManagedPostgresOptions ManagedOptions() => new()
    {
        Mode = DatabaseMode.Managed,
        BundleRoot = Path.Combine(Path.GetTempPath(), "bazaar-bundle-test"),
        DataRoot = Path.Combine(Path.GetTempPath(), "bazaar-data-test"),
    };

    private sealed class MemoryPasswordStore : IManagedPasswordStore
    {
        private string? _password;

        public Task<string?> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(_password);

        public Task SaveAsync(string password, CancellationToken cancellationToken)
        {
            _password = password;
            return Task.CompletedTask;
        }
    }
}
