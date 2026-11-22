using Bazaar.Core;
using Bazaar.ModulePermissions;
using Bazaar.Server.ModuleHost;
using Bazaar.Server.Modules.Health;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bazaar.Server.ModuleHost.Tests;

public sealed class ModuleHealthPollerTests
{
    private sealed class Contract(string id, ModuleHealthStatus live, ModuleHealthStatus ready, bool throws = false) : IModuleHealthContract
    {
        public string ModuleId => id;

        public ValueTask<ModuleHealthCheck> LivenessAsync(CancellationToken cancellationToken) =>
            throws ? throw new InvalidOperationException("boom") : ValueTask.FromResult(new ModuleHealthCheck(live, "live", DateTimeOffset.UnixEpoch));

        public ValueTask<ModuleHealthCheck> ReadinessAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ModuleHealthCheck(ready, "ready", DateTimeOffset.UnixEpoch));

        public ValueTask<IReadOnlyList<ModuleDependencyHealth>> DependenciesAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<ModuleDependencyHealth>>([]);

        public ValueTask<ModuleHealthCheck> SelfTestAsync(ModuleSelfTestScope scope, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ModuleHealthCheck(ModuleHealthStatus.Healthy, "self", DateTimeOffset.UnixEpoch));
    }

    private static async Task<(ModuleActivationHost Host, ModulePermissionGate Gate)> Started()
    {
        var gate = new ModulePermissionGate(new ModuleActivationHostTests.NullSink(), TimeProvider.System);
        var host = new ModuleActivationHost(
            [new ComponentManifest("weather", "1.0.0", ComponentKind.Feature, [], [])],
            new ModuleActivationHostTests.Packages(ModuleActivationHostTests.Package("weather", "gc.post")),
            new ModuleActivationHostTests.Store(ModuleActivationHostTests.Approval("weather", "gc.post")),
            new ModuleActivationHostTests.Signatures(),
            new ModuleActivationHostTests.Owner(),
            gate,
            new ConfigurationBuilder().Build(),
            NullLogger<ModuleActivationHost>.Instance);
        await host.ActivateAllAsync(TestContext.Current.CancellationToken);
        return (host, gate);
    }

    private static ModuleHealthPoller Poller(ModuleActivationHost host, params IModuleHealthContract[] contracts) =>
        new(contracts, host, new ConfigurationBuilder().Build(), TimeProvider.System, NullLogger<ModuleHealthPoller>.Instance);

    [Fact]
    public async Task A_failing_contract_deactivates_its_module()
    {
        var (host, gate) = await Started();

        var changed = await Poller(host, new Contract("weather", ModuleHealthStatus.Healthy, ModuleHealthStatus.Failing)).PollOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal("weather", Assert.Single(changed).ModuleId);
        Assert.Equal(ModuleAccessDecision.DeniedModuleNotRunning, gate.Check("weather", new ModuleAccess("gc.post")));
    }

    [Fact]
    public async Task A_probe_that_throws_counts_as_failing()
    {
        var (host, _) = await Started();

        var changed = await Poller(host, new Contract("weather", ModuleHealthStatus.Healthy, ModuleHealthStatus.Healthy, throws: true)).PollOnceAsync(TestContext.Current.CancellationToken);

        Assert.Single(changed);
    }

    [Fact]
    public async Task Healthy_and_unknown_contracts_change_nothing()
    {
        var (host, gate) = await Started();

        var changed = await Poller(
            host,
            new Contract("weather", ModuleHealthStatus.Healthy, ModuleHealthStatus.Healthy),
            new Contract("not-a-component", ModuleHealthStatus.Failing, ModuleHealthStatus.Failing)).PollOnceAsync(TestContext.Current.CancellationToken);

        Assert.Empty(changed);
        Assert.Equal(ModuleAccessDecision.Allowed, gate.Check("weather", new ModuleAccess("gc.post")));
    }

    [Fact]
    public async Task With_no_contracts_a_poll_does_nothing()
    {
        var (host, _) = await Started();

        Assert.Empty(await Poller(host).PollOnceAsync(TestContext.Current.CancellationToken));
    }
}
