using Bazaar.Components.ManagedPostgres.Secrets;
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
