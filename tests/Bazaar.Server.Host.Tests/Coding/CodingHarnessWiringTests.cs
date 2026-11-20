using Bazaar.Server.CodingGit;
using Bazaar.Server.CodingPermissions;
using Bazaar.Server.Host.Coding;
using Bazaar.Server.Identity;
using Bazaar.Server.Security.Policy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Bazaar.Server.Host.Tests.Coding;

public sealed class CodingHarnessWiringTests
{
    /// <summary>The Server-owned policy and trace services the harness expects its host to have registered.</summary>
    private static ServiceCollection HostPolicyBase()
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
    public void Harness_off_registers_nothing()
    {
        var services = HostPolicyBase();
        var before = services.Count;

        services.AddBazaarCodingHarness(CodingHarnessTestFakes.Config(false));
        services.AddBazaarCodingHarness();

        Assert.Equal(before, services.Count);
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IPushGate));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IHostedService));
    }

    [Fact]
    public async Task Harness_on_with_every_port_bound_passes_the_startup_check_and_resolves()
    {
        var services = HostPolicyBase().AddFakes();
        services.AddBazaarCodingHarness(CodingHarnessTestFakes.Config(true));
        using var provider = services.BuildServiceProvider();

        CodingHarnessPorts.VerifyBound(services);
        foreach (var hosted in provider.GetServices<IHostedService>())
        {
            await hosted.StartAsync(CancellationToken.None);
        }

        Assert.IsType<AgentStatePushSessionDirectory>(provider.GetRequiredService<IPushSessionDirectory>());
        Assert.IsType<PermissionGatePushGate>(provider.GetRequiredService<IPushGate>());
        Assert.IsType<ToolPolicyEngineAdapter>(provider.GetRequiredService<IGenericToolPolicy>());
    }

    [Fact]
    public async Task Harness_on_with_a_missing_port_fails_closed_naming_that_port()
    {
        foreach (var port in CodingHarnessPorts.Required)
        {
            if (port == typeof(IRuntimeSubagentParentDirectory) || port == typeof(IAgentGitIdentity) || port == typeof(IAgentStateReader) || port == typeof(IPushSessionRecords)) { continue; } // bound by the harness itself
            var services = HostPolicyBase().AddFakes(except: port);
            services.AddBazaarCodingHarness(CodingHarnessTestFakes.Config(true));

            var error = Assert.Throws<InvalidOperationException>(() => CodingHarnessPorts.VerifyBound(services));
            Assert.Contains(port.Name, error.Message);

            using var provider = services.BuildServiceProvider();
            var hosted = provider.GetServices<IHostedService>().Single();
            var started = await Assert.ThrowsAsync<InvalidOperationException>(() => hosted.StartAsync(CancellationToken.None));
            Assert.Contains(port.Name, started.Message);
        }
    }

    [Fact]
    public async Task A_quarantined_agent_has_no_push_session()
    {
        var directory = new AgentStatePushSessionDirectory(new QuarantinedAgents(), new NoRecords());
        var request = new PushGateRequest("agent-1", new RepoRef("repo-1", "main"), BranchName.ForTask("task-1"), "gc-1", "task-1");

        Assert.Null(await directory.FindAsync(request, CancellationToken.None));
    }

    private sealed class QuarantinedAgents : IAgentStateReader
    {
        public Bazaar.Server.Security.Taint.SessionTaint TaintFor(string agentId) => Bazaar.Server.Security.Taint.SessionTaint.Clean;

        public bool IsQuarantined(string agentId) => true;
    }

    private sealed class NoRecords : IPushSessionRecords
    {
        public ValueTask<PushSessionRecord?> FindAsync(PushGateRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult<PushSessionRecord?>(null);
    }
}
