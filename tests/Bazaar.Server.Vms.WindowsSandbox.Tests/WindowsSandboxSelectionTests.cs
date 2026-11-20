using Bazaar.Server.Providers;
using Bazaar.Server.Vms.Environments;
using Bazaar.Server.Vms.Lifecycle;

namespace Bazaar.Server.Vms.WindowsSandbox.Tests;

public sealed class WindowsSandboxSelectionTests : IDisposable
{
    private readonly WindowsSandboxBackend _backend = new(
        new FakeWindowsSandboxHost(),
        new FixedHostFacts(FixedHostFacts.WindowsWithSandbox()),
        new WindowsSandboxOptions(Guid.NewGuid()));

    public void Dispose() => _backend.Dispose();

    [Fact]
    public void The_backend_declares_the_module_limit_of_one_instance()
    {
        Assert.Equal(WindowsSandboxModule.Capabilities.MaxConcurrentInstances, _backend.MaxConcurrentInstances);
        Assert.Equal(1, _backend.MaxConcurrentInstances);
    }

    [Fact]
    public void Windows_Sandbox_is_refused_for_the_agent_pair_and_fails_closed_under_auto()
    {
        var selection = ProviderSelector.Select(ProviderRole.Vm, ProviderSelector.Auto, [_backend], ProviderRequirements.AgentPairConcurrency);

        Assert.Null(selection.Provider);
        Assert.Contains("this role needs 2", selection.ToLogLine());
    }

    [Fact]
    public void Windows_Sandbox_is_eligible_for_a_single_instance_role()
    {
        var selection = ProviderSelector.Select(ProviderRole.Vm, WindowsSandboxModule.Kind, [_backend], requiredConcurrency: 1);

        Assert.Same(_backend, selection.Provider);
    }

    [Fact]
    public void Hyper_V_listed_before_Windows_Sandbox_keeps_the_pair_under_auto()
    {
        var hyperV = new AlwaysUsable("hyperv", maxConcurrent: 8);

        var selection = ProviderSelector.Select<IVmProvider>(ProviderRole.Vm, ProviderSelector.Auto, [hyperV, _backend], ProviderRequirements.AgentPairConcurrency);

        Assert.Same(hyperV, selection.Provider);
    }

    [Fact]
    public async Task As_its_own_backend_source_it_is_a_last_resort_pair_candidate_for_windows_guests()
    {
        var lookup = await _backend.GetCurrentAsync(TestContext.Current.CancellationToken);

        Assert.Equal(VmProviderPlacement.Pair, _backend.Placement);
        Assert.Equal(VmBackendOrdering.LastResort, _backend.Preference);
        Assert.Equal([VmOs.Windows], _backend.GuestOs);
        Assert.True(lookup.Usable);
        Assert.Same(_backend, lookup.Backend);
    }

    [Fact]
    public async Task On_a_host_without_the_feature_the_source_keeps_the_backend_but_reports_it_unusable()
    {
        using var backend = new WindowsSandboxBackend(
            new FakeWindowsSandboxHost(),
            new FixedHostFacts(new("linux", "x64", new HashSet<string>())),
            new WindowsSandboxOptions(Guid.NewGuid()));

        var lookup = await backend.GetCurrentAsync(TestContext.Current.CancellationToken);

        Assert.False(lookup.Usable);
        Assert.Same(backend, lookup.Backend);
        Assert.False(string.IsNullOrWhiteSpace(lookup.Reason));
    }

    private sealed class AlwaysUsable(string kind, int maxConcurrent) : IVmProvider, ICapacityLimitedProvider
    {
        public string Kind { get; } = kind;

        public int MaxConcurrentInstances { get; } = maxConcurrent;

        public ProviderStatus Probe() => new(Kind, true, [], null);
    }
}
