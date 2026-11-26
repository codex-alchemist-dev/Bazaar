using Bazaar.Server.Providers;

namespace Bazaar.Server.Vms.WindowsSandbox.Tests;

public sealed class WindowsSandboxCapacityTests : IDisposable
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
    public void The_declared_limit_is_below_the_agent_pair_so_a_consumer_can_refuse_it_for_the_pair()
    {
        Assert.True(_backend.MaxConcurrentInstances < ProviderRequirements.AgentPairConcurrency);
    }

    [Fact]
    public void The_report_lists_the_backend_next_to_another_vm_provider_with_each_status()
    {
        var report = new ProviderCapabilityReport([new OtherVm(), _backend]);

        Assert.Equal(2, report.Entries.Count(e => e.Role == ProviderRole.Vm));
        Assert.Contains(report.Entries, e => e.Kind == WindowsSandboxModule.Kind && e.Role == ProviderRole.Vm);
        Assert.Contains(report.Entries, e => e.Kind == "hyperv" && e.Status.Usable);
    }

    private sealed class OtherVm : IVmProvider
    {
        public string Kind => "hyperv";

        public ProviderStatus Probe() => new(Kind, true, [], null);
    }
}
