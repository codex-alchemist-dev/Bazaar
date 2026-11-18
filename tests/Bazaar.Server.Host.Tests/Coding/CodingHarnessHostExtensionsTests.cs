using Bazaar.Server.CodingEdit;
using Bazaar.Server.CodingGit;
using Bazaar.Server.CodingPermissions;
using Bazaar.Server.Host.Coding;
using Bazaar.Server.Security.Policy;
using Bazaar.Server.Subagents;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Host.Tests.Coding;

public sealed class CodingHarnessHostExtensionsTests
{
    private static ServiceCollection WithPolicy()
    {
        var registry = CapabilityRegistry.Create([]);
        var services = new ServiceCollection();
        services.AddSingleton(registry);
        services.AddSingleton<IToolPolicyEngine>(new ToolPolicyEngine(registry, new InMemoryRateBucketStore(), PolicyOptions.Default));
        services.AddSingleton<IToolCallTrace, InMemoryToolCallTrace>();
        services.AddSingleton<IAgentFreeze, InMemoryAgentFreeze>();
        return services;
    }

    [Fact]
    public void The_permission_gate_resolves_over_the_policy_engine_adapter()
    {
        var services = WithPolicy();
        services.AddBazaarCodingHarness();
        using var provider = services.BuildServiceProvider();

        Assert.IsType<ToolPolicyEngineAdapter>(provider.GetRequiredService<IGenericToolPolicy>());
        Assert.IsType<ToolPermissionGate>(provider.GetRequiredService<IToolPermissionGate>());
    }

    [Fact]
    public void Ports_the_host_must_supply_fail_resolution_instead_of_defaulting_open()
    {
        var services = WithPolicy();
        services.AddBazaarCodingHarness();
        using var provider = services.BuildServiceProvider();

        Assert.Throws<InvalidOperationException>(provider.GetRequiredService<IRuntimeSubagentPort>);
        Assert.Throws<InvalidOperationException>(provider.GetRequiredService<ICodingGitEngine>);
        Assert.Throws<InvalidOperationException>(provider.GetRequiredService<IHostPushPort>);
        Assert.Throws<InvalidOperationException>(provider.GetRequiredService<ISubagentSpawner>);
    }

    [Fact]
    public void The_edit_module_registers_without_a_guest_channel_because_that_is_per_session()
    {
        var services = WithPolicy();
        services.AddBazaarCodingHarness();

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IGuestFileChannel));
    }
}
