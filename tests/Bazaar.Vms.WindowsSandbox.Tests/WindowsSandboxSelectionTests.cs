using Bazaar.Server.Providers;

namespace Bazaar.Vms.WindowsSandbox.Tests;

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

    private sealed class AlwaysUsable(string kind, int maxConcurrent) : IVmProvider, ICapacityLimitedProvider
    {
        public string Kind { get; } = kind;

        public int MaxConcurrentInstances { get; } = maxConcurrent;

        public ProviderStatus Probe() => new(Kind, true, [], null);
    }
}
